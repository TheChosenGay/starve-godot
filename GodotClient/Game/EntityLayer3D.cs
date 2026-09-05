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
    private readonly Dictionary<ulong, ShaderMaterial> _mats = new();
    private readonly Dictionary<ulong, (float X, float Y)> _lastPos = new();
    private readonly Dictionary<ulong, float> _heightSm = new();
    private readonly Dictionary<ulong, long> _flashUntil = new();
    private readonly Dictionary<ulong, long> _footstepAt = new();
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

    public IEnumerable<Node3D> Visuals => _nodes.Values;
    public IReadOnlyDictionary<ulong, Node3D> VisualsById => _nodes;

    public bool TryGetVisual(ulong id, out Node3D node) => _nodes.TryGetValue(id, out node!);

    public void SyncEntities(IReadOnlyDictionary<ulong, EntityView> entities)
    {
        foreach (var id in _nodes.Keys.ToArray())
        {
            if (!entities.ContainsKey(id)) Remove(id);
        }

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
            node.Visible = true;
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
            }
            else
            {
                continue;
            }

            var targetHeight = _tilemap?.HeightAt(p.X, p.Y) ?? 0;
            var h = id == _ownId ? targetHeight : SmoothHeight(id, targetHeight, deltaMs);
            var world = IsoCamera3D.WorldTo3D(p.X, p.Y, h);
            node.Position = new Vector3(world.X, world.Y, world.Z);

            var moving = isMoving(id);
            var dx = 0f;
            var dy = 0f;
            if (_lastPos.TryGetValue(id, out var last))
            {
                dx = p.X - last.X;
                dy = p.Y - last.Y;
            }
            FaceFromIntentOrMotion(id, node, dx, dy, moving, deltaMs);
            _lastPos[id] = (p.X, p.Y);

            if (node is PigmanActor3D pigman)
                pigman.SetLocomotion(moving, id == _ownId ? _ownMoveSpeed : OwnMovementSim.DefaultTilesPerSec);

            if (_flashUntil.TryGetValue(id, out var until))
            {
                var flashing = now < until;
                if (!flashing) _flashUntil.Remove(id);
                if (node is ActorPreview3D preview)
                    preview.SetFlash(flashing);
                else if (node is PigmanActor3D pig)
                    pig.SetFlash(flashing);
                else if (node is TreeActor3D tree)
                    tree.SetFlash(flashing);
                else if (_mats.TryGetValue(id, out var mat))
                    ToonMaterials.SetFlash(mat, flashing);
            }

            if (moving && id == _ownId) MaybeFootstep(id, now);
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
        Flash(entityId);
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

    void IActionPresentationSink.Finish(ulong entityId) { }
    void IActionPresentationSink.Cancel(ulong entityId) { }
    void IActionPresentationSink.Death(ulong entityId) { }

    void IImpactPresentationSink.PlayHit(ulong targetEntity)
    {
        Flash(targetEntity);
        _sfx?.Play("sfx.combat.hit.flesh");
    }

    void IImpactPresentationSink.CorrectPredictedHit(ulong targetEntity, CombatImpactResult result)
    {
        if (_nodes.TryGetValue(targetEntity, out var node) && node is ActorPreview3D preview)
            preview.SetFlash(false);
        else if (_nodes.TryGetValue(targetEntity, out var pigNode) && pigNode is PigmanActor3D pig)
            pig.SetFlash(false);
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

    private void ApplyStyle(ulong id, Node3D node, EntityStyle style)
    {
        if (node is ActorPreview3D or PigmanActor3D or AlchemyEngine3D or TreeActor3D) return;
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
    }

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
