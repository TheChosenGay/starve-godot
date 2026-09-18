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
    // 投掷飞行（带 Thrown 的实体）：位置不走整数格平滑，而是按与服务端同一套抛物线公式采样。
    //
    // 这条跟踪器由 **GameRoot 注入**（`SetThrowFlights`），与它画 ghost 用的是**同一个实例**。
    // 为什么不各自 new 一条：自己的投掷在交接那一刻，ghost 已经飞到 ~1 tick 之后，
    // 而另一条 tracker 才刚拿到权威 elapsed≈0 ⇒ 画面会向后小跳半格。
    // 共用一条时 `Observe(ownThrow: true)` 能看见 ghost 并把它的进度接管过来，交接零跳变。
    //
    // 随之而来的两条纪律：① 快照喂给 tracker 只由 GameRoot 做一次（这里不 Observe）；
    // ② 每帧 Tick 也只由 GameRoot 做（重复推进 = 双倍速）。
    private ThrowFlightTracker? _throwFlights;
    // 上一份 / 本份快照里"正在飞"的权威实体（复用，避免每份快照分配）。
    private HashSet<ulong> _flyingSeen = new();
    private HashSet<ulong> _flyingNow = new();
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

    /// <summary>
    /// 注入投掷飞行跟踪器（与 GameRoot 画 ghost 用的是同一实例）。
    /// 传 null 表示不渲染投掷轨迹（此时飞行体退回普通整数格平滑）。
    /// </summary>
    public void SetThrowFlights(ThrowFlightTracker? tracker) => _throwFlights = tracker;

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
        // 本份快照里"正在飞"的权威实体集合（从共享 tracker 取，不再自己解析 Thrown：
        // 喂快照只由 GameRoot 做一次，这里只消费结果，避免两份状态不一致）。
        _flyingNow.Clear();
        if (_throwFlights is not null)
        {
            var flights = _throwFlights.Samples();
            for (var i = 0; i < flights.Count; i++)
            {
                if (!flights[i].IsGhost) _flyingNow.Add(flights[i].EntityId);
            }
        }

        // 复用缓冲收集待删除项（先收集再改字典，避免迭代中修改）。
        _staleScratch.Clear();
        foreach (var id in _nodes.Keys)
        {
            if (!entities.ContainsKey(id)) _staleScratch.Add(id);
        }
        foreach (var id in _staleScratch) Remove(id);

        foreach (var (id, view) in entities)
        {
            // 上一份还在飞、这一份不在飞了 ⇒ 已落地（或投掷被撤）：把平滑状态重置到落点。
            // 否则下一帧会拿"飞行前的位置"算位移，表现为落地后往投掷者方向滑回/回跳。
            if (_flyingSeen.Contains(id) && !_flyingNow.Contains(id))
            {
                ResetLandingPose(id, entities);
            }

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
            // 火焰的"着/不着"由 HeatSource 组件的存在表达：服务端燃尽时**移除**该组件，
            // 走增量快照的 removed 通道到这里。节点是一次性建好的（ApplyStyle 对火盆节点
            // 直接 return），所以必须每帧按组件存在与否重同步可见性，不能只在建节点时决定一次。
            // 用 style.IsFire 先筛一道：只有火盆节点才有 Flame 子节点，
            // 免得给每帧每个实体都做一次节点路径查找（GC/CPU 都不划算）。
            if (style.IsFire) SyncFlameLit(node, style);
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

        // 交换缓冲：本份成为"上一份"，旧的那份留到下一帧开头清空复用。
        (_flyingSeen, _flyingNow) = (_flyingNow, _flyingSeen);
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

        // 投掷飞行的本地时间由 GameRoot 每帧推一次（共享同一条 tracker，这里再推就是双倍速），
        // 这里只按实体 id 取样本。飞行体在样本里就直接按抛物线摆放。
        Vector3? playerWorld = null;
        if (ownPos is { } approach)
        {
            var playerH = _tilemap?.HeightAt(approach.X, approach.Y) ?? 0;
            var pw = IsoCamera3D.WorldTo3D(approach.X, approach.Y, playerH);
            playerWorld = new Vector3(pw.X, pw.Y, pw.Z);
        }

        foreach (var (id, node) in _nodes)
        {
            // 投掷物：位置/高度完全由抛物线采样决定，跳过整数格平滑与地形高度平滑
            // （后者会把 60FPS 的连续采样又抹成"跟地形的慢半拍"）。
            if (_throwFlights is not null &&
                _throwFlights.TryGet(id, out var flight) && !flight.IsGhost)
            {
                ApplyThrowFlight(id, node, flight);
                continue;
            }

            System.Numerics.Vector2 p;
            var extrap = false;
            var blend = 0f;
            var dt = 0.0;
            var stk = 0L;
            var sn = 0;
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
                extrap = sm.Extrapolating;
                blend = sm.LastBlendDistance;
                dt = sm.LastVirtualTick;
                stk = sm.LatestTick;
                sn = sm.SampleCount;
            }
            else
            {
                continue;
            }

            if (MoveTrace.Enabled) MoveTrace.Sample(id, p.X, p.Y, extrap, blend, dt, stk, sn);
            var vis = VisualWorld(id, p.X, p.Y);
            var targetHeight = _tilemap?.HeightAt(vis.X, vis.Y) ?? 0;
            var h = id == _ownId ? targetHeight : SmoothHeight(id, targetHeight, deltaMs);
            var world = IsoCamera3D.WorldTo3D(vis.X, vis.Y, h);
            node.Position = new Vector3(world.X, world.Y, world.Z);
            // 记录真正画出去的位置与该点地形高度：dRendY 是"上下卡"的直接指标。
            if (MoveTrace.Enabled)
                MoveTrace.SampleRendered(id, world.X, world.Y, world.Z, targetHeight);

            var dx = 0f;
            var dy = 0f;
            if (_lastPos.TryGetValue(id, out var last))
            {
                dx = p.X - last.X;
                dy = p.Y - last.Y;
            }
            var loco = LocomotionPresentation.FromDisplacement(dx, dy, deltaMs, SpeedOf(id));
            // 自己的走路表现逐帧落盘（诊断用）：把"这一帧位移 → moving 判定 → 动画速度"
            // 和 netcode 的校正时间戳对齐，就能分清"是服务器校正导致的卡"还是"动画被打断"。
            if (id == _ownId)
                OwnLocoTrace.Sample(now, dx, dy, deltaMs, loco, _ownDx, _ownDy, p.X, p.Y);
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
            // 投掷在服务端也是"起手→抛出"的挥砍式动作，本地预测沿用同一族动画/音效。
            case ActionKind.Throw:
                _sfx?.Play("sfx.player.swing");
                break;
            // Boss 技能段。音效目录（asset-starve/assets/audio/catalog.json）里目前
            // 没有 Boss 专属素材：能对上现有音效的就映射，对不上的**刻意留空**并写明待补，
            // 不要随便拿一个音效顶上——听感错了比没有更糟。清单见 scripts/README-boss-assets.md。
            case ActionKind.BossThrow:
                // 投弹与投掷同族（起手→抛出，炸弹随后走 Thrown 抛物线复制）：复用挥击起手。
                _sfx?.Play("sfx.player.swing");
                break;
            case ActionKind.BossLeap:
                // 待补 sfx.creature.boss.leap（闪现突进：起手时校验落点、出手时瞬移）。
                break;
            case ActionKind.BossSlam:
                // 待补 sfx.creature.boss.slam。锤地的"画面"已经由 BlastFxLayer3D
                // 消费服务端广播的 BlastEvent（扩散圈 + 震屏）负责，这里只缺音效。
                break;
            case ActionKind.BossRoar:
                // 待补 sfx.creature.boss.roar（嚎叫：进入阶段，无额外事件）。
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

    // 火焰子节点名（FireFlame3D.CreatePit 里挂的就是它）：熄灭只藏它，石头火盆留着，
    // 因为"冷掉的火堆"仍然是个火堆，整块消失反而像被拆了。
    private const string FlameNodeName = "Flame";

    /// <summary>火盆火焰可见性 = 该实体是否带 HeatSource；非火盆节点是空操作。</summary>
    private static void SyncFlameLit(Node3D node, EntityStyle style)
    {
        if (node.GetNodeOrNull<FireFlame3D>(FlameNodeName) is not { } flame) return;
        flame.Visible = style.IsLit;
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

    /// <summary>
    /// 按抛投飞行样本摆放节点：水平位置直接取轨迹采样，高度（离地格数）叠加到世界 Y 上。
    ///
    /// 为什么不用 <see cref="SmoothHeight"/>：抛物线的 Y 每帧都在连续变化，
    /// 再过一道地形高度平滑只会让它滞后于代码算出的轨迹（表现成"浮空/穿地"）。
    /// 这里顺手把 <c>_lastPos</c>/<c>_heightSm</c> 更新到当前采样点，
    /// 这样落地那一帧的位移/高度差是连续的，不会往投掷者方向滑回。
    /// </summary>
    private void ApplyThrowFlight(ulong id, Node3D node, ThrowFlightSample flight)
    {
        var ground = _tilemap?.HeightAt(flight.Position.X, flight.Position.Y) ?? 0f;
        var world = IsoCamera3D.WorldTo3D(flight.Position.X, flight.Position.Y, ground);
        // Height 是"离地高度"，直接加到世界 Y 上（与 ThrowAimLayer3D 的弧线同一口径）。
        world.Y += (float)flight.Height * IsoCamera3D.WorldUnit;
        node.Position = new Vector3(world.X, world.Y, world.Z);

        _lastPos[id] = (flight.Position.X, flight.Position.Y);
        _heightSm[id] = ground;
        if (MoveTrace.Enabled)
            MoveTrace.SampleRendered(id, world.X, world.Y, world.Z, ground);

        // 飞行中的炸弹不该播走路动画（否则会拖着 walk 循环飞）。
        if (node is IAnimatedActor3D actor) actor.SetLocomotion(false, 0f);
    }

    /// <summary>
    /// Thrown 被移除（落地）后把该实体的平滑状态重置到落点。
    /// 服务端落地时 Position 就是落点，直接拿来当"上一帧位置"，
    /// 下一帧的位移判定与高度平滑都从这里重新开始。
    /// </summary>
    private void ResetLandingPose(ulong id, IReadOnlyDictionary<ulong, EntityView> entities)
    {
        if (!entities.TryGetValue(id, out var view)) return;
        if (view.Get("Position", Starve.Game.V1.Position.Parser) is not { } pos) return;
        _lastPos[id] = (pos.X, pos.Y);
        _heightSm[id] = _tilemap?.HeightAt(pos.X, pos.Y) ?? 0f;
    }

    private void Remove(ulong id)
    {
        _actions.Remove(id);
        // 实体整体消失（例如爆炸物落地即销毁）：幂等，GameRoot 的快照 diff 也会 Forget 一次。
        _throwFlights?.Forget(id);
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
