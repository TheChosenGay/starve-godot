using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Starve.Core;
using Starve.Game.V1;
using Starve.Protocol;
using Starve.Protocol.World;
using TileMap = Starve.Core.TileMap;

namespace GodotClient.Game;

/// <summary>
/// 阶段1 实体占位：toon 着色的 3D 胶囊/道具，脚底对齐 WorldTo3D。动作/受击闪白。
/// </summary>
public partial class EntityLayer3D : Node3D, IWorldRenderer, IActionPresentationSink, IImpactPresentationSink
{
    private readonly Dictionary<ulong, Node3D> _nodes = new();
    // 复用缓冲：SyncEntities 每帧都要"找出已消失的实体"，
    // 原先用 _nodes.Keys.ToArray() 会**每帧分配一个新数组**（60fps 下即
    // 每秒 60 次），是托管堆增长与 GC 顿挫的来源之一。见 PerfMonitor 的 GC 计数。
    private readonly List<ulong> _staleScratch = new();
    private readonly Dictionary<ulong, ShaderMaterial> _mats = new();
    private readonly Dictionary<ulong, (float X, float Y)> _lastPos = new();
    private readonly Dictionary<ulong, float> _heightSm = new();
    private readonly Dictionary<ulong, long> _flashUntil = new();
    private readonly Dictionary<ulong, long> _footstepAt = new();
    private readonly Dictionary<ulong, float> _moveSpeed = new();
    private readonly Dictionary<ulong, (int W, int H)> _blockFootprint = new();
    private readonly ActionPresentationController _actions;
    private readonly ImpactPresentationController _impacts;
    private SfxService? _sfx;
    private TileMap? _tilemap;
    private ulong _ownId;
    private int _ownDx;
    private int _ownDy;
    private float _ownFaceX;
    private float _ownFaceY;
    private float _ownMoveSpeed = OwnMovementSim.DefaultTilesPerSec;
    private long _lastNow;
    private bool _plantsVisible = true;

    public EntityLayer3D()
    {
        _actions = new ActionPresentationController(this, NowMs);
        _impacts = new ImpactPresentationController(this);
    }

    public void SetSfx(SfxService? sfx) => _sfx = sfx;
    public void SetOwnId(ulong id) => _ownId = id;
    public void SetNameProvider(Func<EntityView, string?> provider) { }
    public void SetTilemap(TileMap? tm) => _tilemap = tm;
    public void SetViewRotation(float radians) { }
    public void SetDayLight(float dayLight)
    {
        var look = DayCyclePalette.Evaluate(dayLight);
        ToonMaterials.ApplyDayLightToTree(this, look.SunElevation);
    }
    public void SetOwnMoveDir(int dx, int dy)
    {
        _ownDx = dx;
        _ownDy = dy;
    }

    public void SetOwnFacing(float worldX, float worldY)
    {
        _ownFaceX = worldX;
        _ownFaceY = worldY;
    }

    public void SetOwnMoveSpeed(float tilesPerSec) =>
        _ownMoveSpeed = MathF.Max(0f, tilesPerSec);

    public void SetMoveSpeed(ulong id, float tilesPerSec) =>
        _moveSpeed[id] = MathF.Max(0f, tilesPerSec);

    public IEnumerable<Node3D> Visuals => _nodes.Values;
    public IReadOnlyDictionary<ulong, Node3D> VisualsById => _nodes;

    public bool TryGetVisual(ulong id, out Node3D node) => _nodes.TryGetValue(id, out node!);

    public void SyncEntities(IReadOnlyDictionary<ulong, EntityView> entities)
    {
        // 复用缓冲收集待删除项（先收集再改字典，避免迭代中修改）。
        _staleScratch.Clear();
        foreach (var id in _nodes.Keys)
        {
            if (!entities.ContainsKey(id)) _staleScratch.Add(id);
        }
        foreach (var id in _staleScratch) Remove(id);

        foreach (var (id, view) in entities)
        {
            var hasPos = view.Get("Position", Starve.Game.V1.Position.Parser) is not null;
            if (!hasPos)
            {
                if (_nodes.TryGetValue(id, out var hidden)) hidden.Visible = false;
                SyncAction(id, view);
                continue;
            }

            var style = EntityVisual.StyleFor(view);
            if (!_nodes.TryGetValue(id, out var node))
            {
                node = MakeVisual(id, view, style);
                _nodes[id] = node;
                AddChild(node);
            }
            else
            {
                ApplyStyle(id, node, style);
            }
            node.Visible = !EntityVisual.IsDepletedFlower(view);
            RememberBlockFootprint(id, view);
            if (!_plantsVisible && IsPlant(node))
                node.Visible = false;
            // 生物尸体服务端还留约 1 分钟；3D 里非玩家死后立刻藏，掉落是单独的 Loot 实体。
            if (view.Components.ContainsKey("Dead") &&
                view.Get("Player", Player.Parser) is null)
                node.Visible = false;
            SyncAction(id, view);
        }
    }

    public void UpdatePositions(
        IReadOnlyDictionary<ulong, PositionSmoother> smoothers,
        Func<ulong, bool> isMoving,
        long now,
        System.Numerics.Vector2? ownPos = null)
    {
        var deltaMs = _lastNow == 0 ? 16 : now - _lastNow;
        _lastNow = now;
        _actions.Tick();

        Vector3? playerWorld = null;
        if (ownPos is { } approach)
        {
            var playerH = _tilemap?.HeightAt(approach.X, approach.Y) ?? 0;
            var pw = IsoCamera3D.WorldTo3D(approach.X, approach.Y, playerH);
            playerWorld = new Vector3(pw.X, pw.Y, pw.Z);
        }

        foreach (var (id, node) in _nodes)
        {
            System.Numerics.Vector2 p;
            if (id == _ownId && ownPos is { } op)
            {
                p = new System.Numerics.Vector2(op.X, op.Y);
            }
            else if (smoothers.TryGetValue(id, out var sm))
            {
                p = sm.Current(now);
                // 统计外推帧占比：PerfMonitor 每秒汇总一次并清零。
                GameRoot.SmootherSamples++;
                if (sm.Extrapolating) GameRoot.SmootherExtrapolating++;
            }
            else
            {
                continue;
            }

            var vis = VisualWorld(id, p.X, p.Y);
            var targetHeight = _tilemap?.HeightAt(vis.X, vis.Y) ?? 0;
            var h = id == _ownId ? targetHeight : SmoothHeight(id, targetHeight, deltaMs);
            var world = IsoCamera3D.WorldTo3D(vis.X, vis.Y, h);
            node.Position = new Vector3(world.X, world.Y, world.Z);

            var dx = 0f;
            var dy = 0f;
            if (_lastPos.TryGetValue(id, out var last))
            {
                dx = p.X - last.X;
                dy = p.Y - last.Y;
            }
            var loco = LocomotionPresentation.FromDisplacement(dx, dy, deltaMs, SpeedOf(id));
            FaceFromIntentOrMotion(id, node, dx, dy, loco.Moving, deltaMs);
            _lastPos[id] = (p.X, p.Y);

            if (node is IAnimatedActor3D actor)
                actor.SetLocomotion(loco.Moving, loco.TilesPerSec);

            if (_flashUntil.TryGetValue(id, out var until))
            {
                var flashing = now < until;
                if (!flashing) _flashUntil.Remove(id);
                if (node is IAnimatedActor3D flashActor)
                    flashActor.SetFlash(flashing);
                else if (node is TreeActor3D tree)
                    tree.SetFlash(flashing);
                else if (_mats.TryGetValue(id, out var mat))
                    ToonMaterials.SetFlash(mat, flashing);
            }

            if (loco.Moving && id == _ownId) MaybeFootstep(id, now);
        }

        if (playerWorld is { } pp)
        {
            foreach (var node in _nodes.Values)
            {
                if (node is not AlchemyEngine3D engine) continue;
                var dxw = node.Position.X - pp.X;
                var dzw = node.Position.Z - pp.Z;
                engine.NotifyPlayerDistance(MathF.Sqrt(dxw * dxw + dzw * dzw));
            }
        }
    }

    public void PredictAction(ulong id, ActionKind kind, InputCommandRef command) =>
        _actions.Predict(id, kind, command);

    public void PlayLocalAction(ulong id, ActionKind kind)
    {
        if (_nodes.TryGetValue(id, out var node) && node is IAnimatedActor3D actor)
            actor.PlayAction(kind);
    }

    public void CancelPredictedAction(ulong id, ulong requestId) =>
        _actions.CancelPrediction(id, requestId);

    public void CancelActionForMovement(ulong id) => _actions.CancelForMovement(id);

    public void CancelActionLocally(ulong id) => _actions.CancelLocally(id);

    public void ApplyActionOutcome(ActionOutcome outcome) => _actions.ApplyOutcome(outcome);

    public ActionPresentationStatus? ActionStatusOf(ulong id) => _actions.StatusOf(id);

    public void ApplyCombatImpact(WorldEvent worldEvent, CombatImpactEvent impact) =>
        _impacts.Apply(worldEvent, impact);

    public void PredictHit(ulong sourceActionId, ulong targetEntity) =>
        _impacts.PredictHit(sourceActionId, targetEntity);

    void IActionPresentationSink.Apply(ulong entityId, ActionKind kind)
    {
        if (_nodes.TryGetValue(entityId, out var node) && node is IAnimatedActor3D actor)
            actor.PlayAction(kind);
        switch (kind)
        {
            case ActionKind.Attack:
            case ActionKind.Chop:
            case ActionKind.Mine:
                _sfx?.Play("sfx.player.swing");
                break;
            case ActionKind.Pick:
                _sfx?.Play("sfx.gather.pick.berry");
                break;
            case ActionKind.Sleep:
                _sfx?.Play("sfx.player.sleep");
                break;
        }
    }

    void IActionPresentationSink.Finish(ulong entityId)
    {
        if (_nodes.TryGetValue(entityId, out var node) && node is IAnimatedActor3D actor)
            actor.FinishAction();
    }

    void IActionPresentationSink.Cancel(ulong entityId)
    {
        if (_nodes.TryGetValue(entityId, out var node) && node is IAnimatedActor3D actor)
            actor.CancelAction();
    }

    void IActionPresentationSink.Death(ulong entityId)
    {
        if (_nodes.TryGetValue(entityId, out var node) && node is IAnimatedActor3D actor)
            actor.PlayDeath();
    }

    void IImpactPresentationSink.PlayHit(ulong targetEntity)
    {
        Flash(targetEntity);
        if (_nodes.TryGetValue(targetEntity, out var node) && node is IAnimatedActor3D actor)
            actor.PlayHit();
        _sfx?.Play("sfx.combat.hit.flesh");
    }

    void IImpactPresentationSink.CorrectPredictedHit(ulong targetEntity, CombatImpactResult result)
    {
        if (_nodes.TryGetValue(targetEntity, out var node) && node is IAnimatedActor3D actor)
            actor.SetFlash(false);
        else if (_nodes.TryGetValue(targetEntity, out var treeNode) && treeNode is TreeActor3D tree)
            tree.SetFlash(false);
        else if (_mats.TryGetValue(targetEntity, out var mat))
            ToonMaterials.SetFlash(mat, false);
        _flashUntil.Remove(targetEntity);
    }

    void IImpactPresentationSink.PresentNonHit(ulong targetEntity, CombatImpactResult result)
    {
        var id = result switch
        {
            CombatImpactResult.Miss => "sfx.combat.miss",
            CombatImpactResult.Blocked => "sfx.combat.blocked",
            _ => null,
        };
        if (id is not null) _sfx?.Play(id);
    }

    private void SyncAction(ulong id, EntityView view)
    {
        var state = view.Get("ActionState", ActionState.Parser);
        if (state is null)
        {
            _actions.ObserveAbsent(id);
            return;
        }
        _actions.Apply(id, state);
    }

    private Node3D MakeVisual(ulong id, EntityView view, EntityStyle style)
    {
        var actor = ActorCatalog3D.TryCreate(view);
        if (actor is not null)
        {
            actor.Name = $"Entity_{id}";
            return actor;
        }

        if (style.IsWorkbench)
            return new AlchemyEngine3D { Name = $"Entity_{id}" };

        if (style.IsFire)
        {
            var pit = FireFlame3D.CreatePit();
            pit.Name = $"Entity_{id}";
            return pit;
        }

        var node = ActorMesh3D.Create(style);
        node.Name = $"Entity_{id}";
        var mat = ActorMesh3D.MaterialOf(node);
        if (mat is not null) _mats[id] = mat;
        return node;
    }

    public void SetPlantsVisible(bool visible)
    {
        _plantsVisible = visible;
        foreach (var node in _nodes.Values)
        {
            if (!IsPlant(node)) continue;
            node.Visible = visible;
        }
    }

    private static bool IsPlant(Node3D node) =>
        node is TreeActor3D or GhibliPlantActor3D;

    private void ApplyStyle(ulong id, Node3D node, EntityStyle style)
    {
        if (node is IAnimatedActor3D or AlchemyEngine3D or TreeActor3D or GhibliPlantActor3D) return;
        if (node.GetNodeOrNull<FireFlame3D>("Flame") is not null) return;
        ActorMesh3D.ApplyStyle(node, style);
        if (_mats.TryGetValue(id, out var mat) && !_flashUntil.ContainsKey(id))
            ToonMaterials.SetAlbedo(mat, style.Color);
    }

    private void Flash(ulong id) =>
        _flashUntil[id] = NowMs() + 300;

    private void MaybeFootstep(ulong id, long now)
    {
        if (_footstepAt.TryGetValue(id, out var next) && now < next) return;
        _footstepAt[id] = now + 480;
        _sfx?.Play("sfx.player.footstep.grass");
    }

    private void Remove(ulong id)
    {
        _actions.Remove(id);
        if (_nodes.TryGetValue(id, out var n))
        {
            n.QueueFree();
            _nodes.Remove(id);
        }
        _mats.Remove(id);
        _lastPos.Remove(id);
        _heightSm.Remove(id);
        _flashUntil.Remove(id);
        _footstepAt.Remove(id);
        _moveSpeed.Remove(id);
        _blockFootprint.Remove(id);
    }

    /// <summary>取实体节点（调试层用来把碰撞体画在模型身上，跟随同一位置/朝向）。</summary>
    public bool TryGetEntityNode(ulong id, out Node3D node)
    {
        if (_nodes.TryGetValue(id, out var found))
        {
            node = found;
            return true;
        }
        node = null!;
        return false;
    }

    private void RememberBlockFootprint(ulong id, EntityView view)
    {
        var block = view.Get("Block", Block.Parser);
        if (block is null) return;
        _blockFootprint[id] = (Math.Max(1, block.Width), Math.Max(1, block.Height));
    }

    private (float X, float Y) VisualWorld(ulong id, float x, float y)
    {
        if (id == _ownId || !_blockFootprint.TryGetValue(id, out var foot))
            return (x, y);
        return BlockVisual.Center(x, y, foot.W, foot.H);
    }

    private float SpeedOf(ulong id) =>
        id == _ownId
            ? _ownMoveSpeed
            : _moveSpeed.TryGetValue(id, out var speed)
                ? speed
                : OwnMovementSim.DefaultTilesPerSec;

    private const float TurnRadiansPerSec = 10f;

    private void FaceFromIntentOrMotion(ulong id, Node3D node, float dx, float dy, bool moving, float deltaMs)
    {
        if (node is AlchemyEngine3D) return;
        if (node.GetNodeOrNull<FireFlame3D>("Flame") is not null) return;
        float yaw;
        if (id == _ownId)
        {
            if (MathF.Abs(_ownFaceX) + MathF.Abs(_ownFaceY) >= 0.01f)
                yaw = IsoCamera3D.FacingYaw(_ownFaceX, _ownFaceY);
            else if (_ownDx != 0 || _ownDy != 0)
                yaw = IsoCamera3D.FacingYaw(_ownDx, _ownDy);
            else
                return;
        }
        else
        {
            if (!moving || MathF.Abs(dx) + MathF.Abs(dy) <= 0.08f) return;
            yaw = IsoCamera3D.FacingYaw(dx, dy);
        }
        var step = TurnRadiansPerSec * MathF.Max(deltaMs, 1f) / 1000f;
        var next = Mathf.RotateToward(node.Rotation.Y, yaw, step);
        node.Rotation = new Vector3(0, next, 0);
    }

    private float SmoothHeight(ulong id, float target, float deltaMs)
    {
        if (_heightSm.TryGetValue(id, out var prev))
        {
            var k = Mathf.Min(1f, deltaMs / 40f);
            target = prev + (target - prev) * k;
        }
        _heightSm[id] = target;
        return target;
    }

    private static long NowMs() => checked((long)Time.GetTicksMsec());
}
