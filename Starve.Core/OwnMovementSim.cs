using System;
using System.Collections.Generic;

namespace Starve.Core;

public readonly record struct MovementDiagnostics(
    float LastReconciliationError,
    float MaxReconciliationError,
    int SoftCorrections,
    int HardSnaps);

/// <summary>
/// 自己的本地移动预测：按下立即移动（不等服务端 50ms tick + 快照往返）。
/// 预测顺序与服务端 systems.MoveBody 完全一致：形状层（扫掠 + 沿接触切面滑动）→
/// 格子层（地形水/悬崖跨格校验，不可走贴边停）。占位物（树/岩/建筑）不写阻挡网格——
/// 它们只提供形状，所以本地预测要拿到 <see cref="SetBlockers"/> 的形状列表。
/// 服务端快照到达时做误差校正（大误差瞬移、小误差缓合）。纯逻辑，时间由外部注入。
/// </summary>
/// <summary>
/// 本端预测/和解的**调用面**（GameRoot/MoveTrace 用）。
///
/// 两个实现：
/// <list type="bullet">
/// <item><see cref="OwnMovementSim"/>：旧的字段式实现（保留兼容/对照）；</item>
/// <item><see cref="ComponentOwnMovementSim"/>：新的组件实现（<c>ClientSmoother</c> +
///   <c>OwnMovePredictor</c>，序号锚定和解、追步/冗余上行都在组件里）。</item>
/// </list>
/// 抽接口是为了让调用方**一行都不用改**就能切换实现。
/// </summary>
public interface IOwnMovementSim
{
    Func<float, float, float>? HeightAt { get; set; }
    OwnMovePredictor Predictor { get; }
    bool Has { get; }
    bool Moving { get; }
    (float X, float Y) Position { get; }
    (float X, float Y) Velocity { get; }
    MovementDiagnostics Diagnostics { get; }
    (int Dx, int Dy) Intent { get; }
    float LastSlope { get; }
    OwnMovementSim.ReconcileTrace LastReconcile { get; }

    void SnapTo(float x, float y);
    void SetIntent(int dx, int dy);
    void SetSpeed(float tilesPerSec);
    void SetBodyRadius(float radius);
    void SetBlockers(IReadOnlyList<BlockerShape> blockers);
    void SetNeighbors(IReadOnlyList<OrcaNeighbor> neighbors);
    void SetSelfKey(ulong entityId);
    void SetSpeedProfile(float effectiveSpeed, float halfLength);

    /// <summary>推进一帧。<paramref name="nowMs"/> 是**绝对墙钟**（组件用它映射服务端 tick）。</summary>
    void Tick(float dtMs, long nowMs);

    /// <summary>收到权威快照：<paramref name="appliedSeq"/> 是服务端"已消费到第几条操作"。</summary>
    void Reconcile(float x, float y, bool stopped, long tick, ulong appliedSeq, ulong epoch, long nowMs);
    void FeedServerMotion(float velX, float velY, float effectiveSpeed, int dirX, int dirY, int pathLen);
}

public sealed class OwnMovementSim : IOwnMovementSim
{
    /// <summary>默认速度：与服务端一致 10 格/秒（快照 Moveable.speed 会覆盖）。</summary>
    public const float DefaultTilesPerSec = 10f;

    /// <summary>缺省移动体碰撞半径（格）：与服务端 systems.BodyRadius 一致（= configs/models.json 里 player 推导值）。</summary>
    public const float DefaultBodyRadius = 0.305f;
    private readonly OwnMovePredictor _predictor;
    private float _velX, _velY;
    private int _anchorX;
    private int _anchorY;
    private float _subX;
    private float _subY;
    private int _dirX;
    private int _dirY;
    private bool _has;
    private float _lastReconciliationError;
    private float _maxReconciliationError;
    private int _softCorrections;
    private int _hardSnaps;
    // 已对哪一 tick 的服务端位置收敛过（-1 = 还没收敛过）；用于丢弃陈旧快照。
    private long _lastReconciledTick = -1;
    // 停止是否已经落定：落定之后同 tick/更旧的旧快照不再参与校正。
    private bool _stopSettled;

    public Func<float, float, float>? HeightAt
    {
        get => _predictor.HeightAt;
        set => _predictor.HeightAt = value;
    }

    public OwnMovementSim(Func<int, int, bool> walkable) =>
        _predictor = new OwnMovePredictor(walkable);

    /// <summary>
    /// 真正的移动数学（与服务端同构，实现组件的 <c>INetModel</c>）。
    /// 本类只是**旧 API 的门面**：保留字段式状态以便既有测试与调用方平滑迁移；
    /// 新代码应当直接用 <see cref="OwnMovePredictor"/> + <c>ClientSmoother</c>。
    /// </summary>
    public OwnMovePredictor Predictor => _predictor;

    public bool Has => _has;
    public bool Moving => _dirX != 0 || _dirY != 0;
    public (float X, float Y) Position => (_anchorX + _subX, _anchorY + _subY);
    public MovementDiagnostics Diagnostics => new(
        _lastReconciliationError,
        _maxReconciliationError,
        _softCorrections,
        _hardSnaps);

    /// <summary>出生/瞬移/大误差：直接贴合服务端位置。</summary>
    public void SnapTo(float x, float y)
    {
        SetRealPosition(x, y);
        _has = true;
        // 出生/瞬移/硬跳后，之前记录的新鲜度基线作废：下一份快照应被当作新的。
        _lastReconciledTick = -1;
        _stopSettled = false;
    }

    /// <summary>输入方向（0,0 = 停）；与发送给服务端的命令一致。</summary>
    public void SetIntent(int dx, int dy)
    {
        _dirX = dx;
        _dirY = dy;
    }

    /// <summary>同步服务端效果修正后的权威速度；0 表示被冻结。</summary>
    public void SetSpeed(float tilesPerSec) => _predictor.SetSpeed(tilesPerSec);

    /// <summary>
    /// 同步服务端下发的实体碰撞半径（Moveable.body_radius，由客户端模型推导）。
    /// 半径不一致就会贴着树/墙来回校正，所以必须用服务端的值，不要用客户端常量。
    /// </summary>
    public void SetBodyRadius(float radius) => _predictor.SetBodyRadius(radius);

    /// <summary>
    /// 同步占位物形状（快照里的 Block：圆=格心圆，盒=占格矩形）。
    /// 形状变了就调一次；空列表 = 本帧没有已知占位物。
    /// </summary>
    public void SetBlockers(IReadOnlyList<BlockerShape> blockers) =>
        _predictor.SetBlockers(blockers);

    /// <summary>
    /// 动态邻居（ORCA 用）：位置/速度/半径。由外部每帧从快照刷新。
    /// 只放**其他**动态实体（玩家/动物），不放静态障碍——静态走硬碰撞（阶段②）。
    /// </summary>
    public void SetNeighbors(IReadOnlyList<OrcaNeighbor> neighbors) =>
        _predictor.SetNeighbors(neighbors);

    /// <summary>本 tick 的实际速度（格/秒）：ORCA 的"当前速度"输入，也是表现层依据。</summary>
    public (float X, float Y) Velocity => (_velX, _velY);

    /// <summary>设置自己的实体 id（ORCA 对称打破用；须与服务端下发的 id 一致）。</summary>
    public void SetSelfKey(ulong entityId) => _predictor.SetSelfKey(entityId);

    /// <summary>同步服务端下发的有效速度与胶囊半长（ORCA 的输入维度）。</summary>
    public void SetSpeedProfile(float effectiveSpeed, float halfLength) =>
        _predictor.SetSpeedProfile(effectiveSpeed, halfLength);

    /// <summary>
    /// 推进一帧，与服务端三阶段移动同公式：
    /// ① desired：位移 = effective_speed × 坡度因子 × dt，对角归一化（÷√2）；
    /// ② slide  ：对**静态**障碍扫掠 + 沿接触切面投影（硬约束，绝不穿模）；
    /// ③ avoid  ：ORCA 对其他动态体避让，产出方向任意的最终速度；
    /// 最后走格子层（跨格校验地形可走）。
    ///
    /// 这三步的顺序与服务端 systems/move_solver.go 严格一致；ORCA 也是同一套公式
    /// （Starve.Core/OrcaAvoidance.cs 是 internal/game/systems/orca.go 的移植）。
    /// </summary>
    /// <summary>旧签名（无绝对时钟）：组件实现必须用带 nowMs 的那个。</summary>
    public void Tick(float dtMs) => Tick(dtMs, 0);

    /// <summary>推进一帧（<paramref name="nowMs"/> 本实现不用，接口对齐用）。</summary>
    public void Tick(float dtMs, long nowMs)
    {
        if (!_has || dtMs <= 0) return;
        if (_dirX == 0 && _dirY == 0)
        {
            // 连续移动允许停在任意子格；服务端同样保留 sub，不吸附整数格。
            return;
        }

        var predStartX = Position.X;
        var predStartY = Position.Y;

        // 数学全部在 OwnMovePredictor（与组件重放共用同一段）；这里只搬状态进出。
        var state = ToState();
        var intent = new MoveIntent(_dirX, _dirY);
        // 渲染帧可能卡顿超过一个服务端 tick；按 50ms 分片推进，避免一次跨越多个格子。
        var remainingMs = dtMs;
        while (remainingMs > 0)
        {
            var sliceMs = MathF.Min(remainingMs, 50f);
            _predictor.Step(ref state, intent, sliceMs / 1000.0, 0);
            remainingMs -= sliceMs;
        }

        FromState(state);

        // 累计"预测自己走了多少"（不含 Reconcile 的校正），供快照做同跨度比较。
        _predStepX += Position.X - predStartX;
        _predStepY += Position.Y - predStartY;
    }

    private OwnMoveState ToState() => new()
    {
        AnchorX = _anchorX,
        AnchorY = _anchorY,
        SubX = _subX,
        SubY = _subY,
        VelX = _velX,
        VelY = _velY,
    };

    private void FromState(in OwnMoveState state)
    {
        _anchorX = state.AnchorX;
        _anchorY = state.AnchorY;
        _subX = state.SubX;
        _subY = state.SubY;
        _velX = state.VelX;
        _velY = state.VelY;
    }

    private void SetRealPosition(float x, float y)
    {
        _anchorX = (int)MathF.Floor(x);
        _anchorY = (int)MathF.Floor(y);
        _subX = x - _anchorX;
        _subY = y - _anchorY;
        if (_subX < 0)
        {
            _anchorX--;
            _subX += 1;
        }
        if (_subY < 0)
        {
            _anchorY--;
            _subY += 1;
        }
    }

    private void BlendTo(float x, float y, float factor)
    {
        var current = Position;
        SetRealPosition(
            current.X + (x - current.X) * factor,
            current.Y + (y - current.Y) * factor);
    }

    /// <summary>本次和解做了什么决定（诊断/日志用）。</summary>
    public enum ReconcileDecision
    {
        /// <summary>还没调用过。</summary>
        None,
        /// <summary>首帧/出生：直接贴合。</summary>
        InitialSnap,
        /// <summary>陈旧快照（tick 未推进或更旧）：不理会。</summary>
        Stale,
        /// <summary>误差在死区内：不校正。</summary>
        NoError,
        /// <summary>小步缓合（k&lt;1）。</summary>
        Soft,
        /// <summary>大误差瞬移（&gt;4 格）。</summary>
        HardSnap,
        /// <summary>服务端确认停止：当前实现用 k=1 一次到位吸附。</summary>
        StopSnap,
    }

    /// <summary>
    /// 一次和解的完整快照（诊断用，纯数据）。
    ///
    /// 关键设计：<b>速度比较必须用"同 tick 跨度的位移"</b>，不能拿"本地现在"比"服务端过去"——
    /// 后者天然含网络领先量（≈ v×往返延迟），会把延迟误判成失配。
    /// Step* 字段就是"相邻两次快照之间"服务端走了多少 / 本地走了多少，与延迟无关。
    /// </summary>
    public readonly record struct ReconcileTrace(
        ReconcileDecision Decision,
        long ServerTick,
        float ServerX,
        float ServerY,
        float ServerVelX,
        float ServerVelY,
        float ServerVelSpeed,
        float ServerEffectiveSpeed,
        int ServerDirX,
        int ServerDirY,
        int ServerPathLen,
        bool ServerStopped,
        float LocalX,
        float LocalY,
        int LocalDirX,
        int LocalDirY,
        float Slope,
        float ErrX,
        float ErrY,
        float Err,
        float ImpliedLeadMs,
        bool HasStep,
        int StepTicks,
        float ServerStepX,
        float ServerStepY,
        float ServerStepSpeed,
        float LocalStepX,
        float LocalStepY,
        float LocalStepSpeed,
        float DriftX,
        float DriftY,
        float DriftSpeed,
        float K,
        int SoftCorrections,
        int HardSnaps,
        bool StopSettled);

    /// <summary>最近一次和解的完整快照；没调用过时 Decision = None。</summary>
    public ReconcileTrace LastReconcile { get; private set; }

    /// <summary>当前本地意图（诊断/日志用）。</summary>
    public (int Dx, int Dy) Intent => (_dirX, _dirY);

    /// <summary>最近一个推进切片实际用的坡度因子（诊断/日志用）。</summary>
    public float LastSlope => _predictor.LastSlope;

    // 服务端权威"行为"（每个快照刷新一次，仅供诊断与和解参考）。
    private float _srvVelX, _srvVelY, _srvEffectiveSpeed;
    private int _srvDirX, _srvDirY, _srvPathLen;

    // 两次快照之间"纯预测"产生的位移（累计于 Tick，Reconcile 时消费并清零）。
    // 关键：必须把**校正**排除在外，否则测出来的不是"预测速度"而是"净位移速度"，
    // 校正本身会被误记成预测偏差。见 ReconcileTrace 注释。
    private float _predStepX;
    private float _predStepY;

    // 相邻两次快照的对照基线（用于"同跨度位移"比较）。
    private bool _hasSnapBase;
    private long _snapBaseTick;
    private float _snapBaseSrvX, _snapBaseSrvY;

    /// <summary>
    /// 同步服务端下发的权威运动状态（Moveable）：速度、效果速度、意图方向、路径长度。
    /// 只用于诊断与和解参考，不影响本地预测公式（预测公式靠 SetSpeed/SetIntent）。
    /// </summary>
    public void FeedServerMotion(
        float velX, float velY, float effectiveSpeed, int dirX, int dirY, int pathLen)
    {
        _srvVelX = velX;
        _srvVelY = velY;
        _srvEffectiveSpeed = effectiveSpeed;
        _srvDirX = dirX;
        _srvDirY = dirY;
        _srvPathLen = pathLen;
    }

    /// <summary>
    /// 服务端快照校正：只有真正的瞬移/传送（&gt;4 格）才硬跳。
    /// serverStopped = 服务端已确认停止（Moveable.Dir=0 且路径为空）。
    /// serverTick = 这份位置是哪一 tick 下发的（-1 = 未知，按"新鲜"处理，兼容旧调用方）。
    ///
    /// 陈旧判定（修"停下抖动"）：增量快照只带脏组件，玩家停下后服务端不再标脏
    /// Position/Moveable，这两个组件会一直停留在停下那一刻的值，而每次增量仍会让
    /// Revision +1、仍会调用本函数。如果反复拿这份冻结值收敛，就会"停下后被回拉"，
    /// 下次移动时又被往前拽——即抖动。所以：
    ///   - 已经对某个 tick 的位置收敛过停止 → 同一 tick（更旧）再调用直接忽略；
    ///   - 位置比上次收敛的更旧 → 同样忽略。
    /// 这样停止只收敛一次，之后完全不干预本地预测。
    /// </summary>
    public void Reconcile(
        float serverX, float serverY, bool serverStopped = false, long serverTick = -1)
        => Reconcile(serverX, serverY, serverStopped, serverTick, 0, 0, 0);

    /// <summary>接口版本：多出的 <paramref name="appliedSeq"/>/<paramref name="epoch"/>/<paramref name="nowMs"/>
    /// 本实现不用（旧的字段式和解只吃位置/tick），组件实现才用。</summary>
    public void Reconcile(
        float serverX, float serverY, bool serverStopped, long serverTick,
        ulong appliedSeq, ulong epoch, long nowMs)
    {
        // 校正前的本地位置：诊断要用它算"本地这一步走了多少"，
        // 必须在 ReconcileCore 动位置之前取。
        var localBefore = Position;
        var r = ReconcileCore(serverX, serverY, serverStopped, serverTick);
        RecordReconcile(serverX, serverY, serverStopped, serverTick, localBefore, r);
    }

    private (ReconcileDecision Decision, float K, float ErrX, float ErrY, float Err) ReconcileCore(
        float serverX, float serverY, bool serverStopped, long serverTick)
    {
        var current = Position;
        var errX = serverX - current.X;
        var errY = serverY - current.Y;
        var err = MathF.Sqrt(errX * errX + errY * errY);

        if (!_has)
        {
            SnapTo(serverX, serverY);
            _lastReconciledTick = serverTick;
            _stopSettled = serverStopped;
            return (ReconcileDecision.InitialSnap, 0f, errX, errY, err);
        }
        // 陈旧数据：这份位置不包含新信息，收敛它只会把角色往回拉。
        var stale = serverTick >= 0 && _lastReconciledTick >= 0 && serverTick <= _lastReconciledTick;
        if (stale)
        {
            if (serverStopped && _stopSettled) return (ReconcileDecision.Stale, 0f, errX, errY, err);
            if (!serverStopped) return (ReconcileDecision.Stale, 0f, errX, errY, err);
        }
        _lastReconciliationError = err;
        _maxReconciliationError = MathF.Max(_maxReconciliationError, err);
        if (err > 4f)
        {
            _hardSnaps++;
            SnapTo(serverX, serverY);
            _lastReconciledTick = serverTick;
            _stopSettled = serverStopped;
            return (ReconcileDecision.HardSnap, 1f, errX, errY, err);
        }
        var k = 0f;
        if (serverStopped)
        {
            // 停止落定：死区 0.15 格，超过后快速合拢（最快 50%/快照 ≈ 100ms 内到位），
            // 小误差不再慢慢拽（那是"停下回拉"的来源）。
            k = err > 0.15f ? MathF.Min(0.5f, 0.25f + err * 0.18f) : 0f;
        }
        else if (err > 0.75f)
        {
            k = 0.2f;
        }
        if (k <= 0)
        {
            _lastReconciledTick = serverTick;
            _stopSettled = serverStopped;
            return (ReconcileDecision.NoError, 0f, errX, errY, err);
        }
        _softCorrections++;
        // 停止落定用一次到位（k=1）而不是渐进逼近：渐进会把"回拉"摊成好几次
        // 20Hz 跳变，正是看得见的抖动。移动中仍保留小步收敛，避免突兀。
        BlendTo(serverX, serverY, serverStopped ? 1f : k);
        _lastReconciledTick = serverTick;
        _stopSettled = serverStopped;
        return (
            serverStopped ? ReconcileDecision.StopSnap : ReconcileDecision.Soft,
            serverStopped ? 1f : k,
            errX, errY, err);
    }

    private void RecordReconcile(
        float serverX,
        float serverY,
        bool serverStopped,
        long serverTick,
        (float X, float Y) localBefore,
        (ReconcileDecision Decision, float K, float ErrX, float ErrY, float Err) r)
    {
        var velSpeed = MathF.Sqrt(_srvVelX * _srvVelX + _srvVelY * _srvVelY);
        float srvStepX = 0f, srvStepY = 0f, locStepX = 0f, locStepY = 0f;
        float srvStepSpeed = 0f, locStepSpeed = 0f;
        var stepTicks = 0;
        var hasStep = _hasSnapBase && serverTick > _snapBaseTick;
        // 本地这一步用"纯预测"累加器（不含校正），才是可与服务端比较的预测速度。
        locStepX = _predStepX;
        locStepY = _predStepY;
        _predStepX = 0f;
        _predStepY = 0f;
        if (hasStep)
        {
            stepTicks = (int)(serverTick - _snapBaseTick);
            srvStepX = serverX - _snapBaseSrvX;
            srvStepY = serverY - _snapBaseSrvY;
            // tick 跨度 → 秒：stepTicks 个 tick，每个 50ms（20Hz）。
            var perSec = 20f / stepTicks;
            srvStepSpeed = MathF.Sqrt(srvStepX * srvStepX + srvStepY * srvStepY) * perSec;
            locStepSpeed = MathF.Sqrt(locStepX * locStepX + locStepY * locStepY) * perSec;
        }

        var driftPerSec = hasStep ? 20f / stepTicks : 0f;
        var driftX = (locStepX - srvStepX) * driftPerSec;
        var driftY = (locStepY - srvStepY) * driftPerSec;

        LastReconcile = new ReconcileTrace(
            r.Decision,
            serverTick,
            serverX, serverY,
            _srvVelX, _srvVelY, velSpeed, _srvEffectiveSpeed,
            _srvDirX, _srvDirY, _srvPathLen, serverStopped,
            localBefore.X, localBefore.Y, _dirX, _dirY, LastSlope,
            r.ErrX, r.ErrY, r.Err,
            locStepSpeed > 0.05f ? r.Err / locStepSpeed * 1000f : 0f,
            hasStep, stepTicks,
            srvStepX, srvStepY, srvStepSpeed,
            locStepX, locStepY, locStepSpeed,
            driftX, driftY, locStepSpeed - srvStepSpeed,
            r.K, _softCorrections, _hardSnaps, _stopSettled);

        _hasSnapBase = true;
        _snapBaseTick = serverTick;
        _snapBaseSrvX = serverX;
        _snapBaseSrvY = serverY;
    }
}
