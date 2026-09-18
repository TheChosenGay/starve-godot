using Starve.Netcode;

namespace Starve.Core;

/// <summary>
/// <see cref="IOwnMovementSim"/> 的**组件实现**：内部就是 <c>ClientSmoother&lt;OwnMoveState, MoveIntent&gt;</c>
/// + <see cref="OwnMovePredictor"/>。抽这一层是为了让 GameRoot/MoveTrace 的既有调用面一行不用改，
/// 就能从旧的字段式实现切到"序号锚定预测/和解"。
///
/// 关键语义（与旧实现对齐的地方）：
/// <list type="bullet">
/// <item><see cref="Position"/> = **渲染位置**（模拟状态 + 残差平滑 + 帧余量外推，纯表现层）；</item>
/// <item><see cref="Tick"/> 用**绝对墙钟**推进（组件的服务端时钟靠它映射 tick，不能用累加的 delta）；</item>
/// <item><see cref="Reconcile"/> 需要 <c>appliedSeq</c>/<c>epoch</c> —— 序号锚定和解就是靠"已消费到第几条操作"对齐的；</item>
/// <item><see cref="SetIntent"/> 只设置当前意图：操作由组件**按 tick 采样**产生（长按每 tick 一条）；</item>
/// <item><see cref="LastReconcile"/> 里沿用旧 <c>ReconcileTrace</c> 结构，但组件模式下
///   "步长/漂移/领先毫秒"这类旧口径字段留 0 —— 那套统计属于旧实现。**逐次和解的权威记录**
///   看组件指标日志（<c>NetcodeMetrics</c>，每次和解一行）。</item>
/// </list>
/// </summary>
public sealed class ComponentOwnMovementSim : IOwnMovementSim
{
    /// <summary>是否启用组件模式（默认开；设 <c>STARVE_LEGACY_OWN_SIM=1</c> 可回退到旧实现对照）。</summary>
    public static bool Enabled { get; } =
        Environment.GetEnvironmentVariable("STARVE_LEGACY_OWN_SIM") != "1";

    private readonly OwnMovePredictor _predictor;
    private readonly ClientSmoother<OwnMoveState, MoveIntent> _smoother;
    private readonly List<ClientSmoother<OwnMoveState, MoveIntent>.OpRef> _ops = new();

    private int _dirX;
    private int _dirY;
    private long _nowMs;
    private ulong _epoch;
    private float _lastError;
    private float _maxError;
    private int _soft;
    private int _hard;
    private OwnMovementSim.ReconcileTrace _lastReconcile;

    // FeedServerMotion 存下来的服务端运动信息（只用于诊断 trace）。
    private float _srvVelX, _srvVelY, _srvEffectiveSpeed;
    private int _srvDirX, _srvDirY, _srvPathLen;

    public ComponentOwnMovementSim(Func<int, int, bool> walkable)
    {
        _predictor = new OwnMovePredictor(walkable);
        _smoother = new ClientSmoother<OwnMoveState, MoveIntent>(
            _predictor,
            new NetcodeConfig
            {
                TickIndexedIntents = true,
                // 手感 A/B：STARVE_OWN_RENDER_DELAY_TICKS=0 回到"外推"（零额外延迟但会回抽），
                // 1 = 默认"tick 间插值"（更顺，显示上晚一个 tick）。0.5 = 折中。
                OwnRenderDelayTicks =
                    float.TryParse(
                        System.Environment.GetEnvironmentVariable("STARVE_OWN_RENDER_DELAY_TICKS"),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var d)
                        ? d
                        : 1.0,
            });
    }

    /// <summary>组件本体（诊断/指标接线用）。</summary>
    public ClientSmoother<OwnMoveState, MoveIntent> Smoother => _smoother;

    /// <summary>组件配置（上行冗余条数等；调用方直接读这里，别再各自写死常数）。</summary>
    public NetcodeConfig Config => _smoother.Config;

    /// <summary>取"服务端还没确认的操作"（上行冗余窗口；调用方把它交给 CommandService.SendMoveOps）。</summary>
    public int CollectUnackedOps(List<ClientSmoother<OwnMoveState, MoveIntent>.OpRef> into, int max = 3) =>
        _smoother.CollectUnackedOps(into, max);

    /// <summary>
    /// 为一条**离散操作**（攻击/合成/取消…）预留序号：与移动共用同一条输入流
    /// （服务端按序号连续消费），但它不占移动步、也不走移动冗余通道。
    /// 传给 <c>CommandService.SeqSource</c> 即可让所有操作统一编号。
    /// </summary>
    public ulong ReserveDiscreteOp(long nowMs) => _smoother.ReserveDiscreteOp(nowMs > 0 ? nowMs : _nowMs);

    public Func<float, float, float>? HeightAt
    {
        get => _predictor.HeightAt;
        set => _predictor.HeightAt = value;
    }

    public OwnMovePredictor Predictor => _predictor;
    public bool Has => _smoother.HasState;
    public bool Moving => _dirX != 0 || _dirY != 0;

    /// <summary>渲染位置（残差平滑 + tick 间插值）：画面/相机读这个，比模拟状态晚约一个 tick。</summary>
    public (float X, float Y) Position
    {
        get
        {
            var r = _smoother.RenderedState;
            return (r.X, r.Y);
        }
    }

    /// <summary>
    /// **模拟状态**（权威正确、无渲染滞后）：玩法判定/上报/诊断读这个。
    ///
    /// 与 <see cref="Position"/> 的区别就是"渲染插值"：默认渲染在"上一条 tick → 最新 tick"之间插值
    /// （见 <see cref="NetcodeConfig.OwnRenderDelayTicks"/>），所以会滞后最多一个 tick。
    /// </summary>
    public (float X, float Y) SimPosition
    {
        get
        {
            var st = _smoother.State;
            return (st.X, st.Y);
        }
    }

    public (float X, float Y) Velocity
    {
        get
        {
            var s = _smoother.State;
            return (s.VelX, s.VelY);
        }
    }

    public MovementDiagnostics Diagnostics => new(_lastError, _maxError, _soft, _hard);
    public (int Dx, int Dy) Intent => (_dirX, _dirY);
    public float LastSlope => _predictor.LastSlope;
    public OwnMovementSim.ReconcileTrace LastReconcile => _lastReconcile;

    public void SnapTo(float x, float y) => _smoother.ForceSnapTo(OwnMoveState.FromContinuous(x, y), _nowMs);

    public void SetIntent(int dx, int dy)
    {
        _dirX = dx;
        _dirY = dy;
        _smoother.SetIntent(0, _epoch, new MoveIntent(dx, dy), _nowMs);
    }

    public void SetSpeed(float tilesPerSec) => _predictor.SetSpeed(tilesPerSec);
    public void SetBodyRadius(float radius) => _predictor.SetBodyRadius(radius);
    public void SetBlockers(IReadOnlyList<BlockerShape> blockers) => _predictor.SetBlockers(blockers);
    public void SetNeighbors(IReadOnlyList<OrcaNeighbor> neighbors) => _predictor.SetNeighbors(neighbors);
    public void SetSelfKey(ulong entityId) => _predictor.SetSelfKey(entityId);
    public void SetSpeedProfile(float effectiveSpeed, float halfLength) =>
        _predictor.SetSpeedProfile(effectiveSpeed, halfLength);

    public void Tick(float dtMs, long nowMs)
    {
        // 绝对墙钟优先；调用方没给（旧签名）时用累加值兜底。
        _nowMs = nowMs > 0 ? nowMs : _nowMs + (long)dtMs;
        _smoother.AdvanceFrame(_nowMs);
    }

    public void Reconcile(float x, float y, bool stopped, long tick, ulong appliedSeq, ulong epoch, long nowMs)
    {
        _epoch = epoch != 0 ? epoch : _epoch;
        if (nowMs > 0) _nowMs = nowMs;

        var before = Position;
        var report = _smoother.OnSnapshot(
            new NetSnapshot<OwnMoveState>(tick, appliedSeq, _epoch, OwnMoveState.FromContinuous(x, y), stopped),
            _nowMs);

        _lastError = report.Err;
        if (report.Err > _maxError) _maxError = report.Err;
        if (report.Kind == CorrectionKind.Blended) _soft++;
        if (report.Kind == CorrectionKind.Snapped) _hard++;

        var after = Position;
        var decision = report.Kind switch
        {
            CorrectionKind.Bootstrap => OwnMovementSim.ReconcileDecision.InitialSnap,
            CorrectionKind.Stale => OwnMovementSim.ReconcileDecision.Stale,
            CorrectionKind.Deadzone => OwnMovementSim.ReconcileDecision.NoError,
            CorrectionKind.Blended => OwnMovementSim.ReconcileDecision.Soft,
            CorrectionKind.Snapped => OwnMovementSim.ReconcileDecision.HardSnap,
            _ => OwnMovementSim.ReconcileDecision.None,
        };
        // 旧 trace 的字段比组件报告多（那些是旧实现自己的统计口径）：这里只填**能对上**的，
        // 其余留 0 —— MoveTrace 读的就是这些字段，组件模式下"步长/漂移"那些口径由报告替代。
        _lastReconcile = new OwnMovementSim.ReconcileTrace(
            decision, report.SnapshotTick, x, y,
            _srvVelX, _srvVelY, MathF.Sqrt(_srvVelX * _srvVelX + _srvVelY * _srvVelY), _srvEffectiveSpeed,
            _srvDirX, _srvDirY, _srvPathLen, stopped,
            after.X, after.Y, _dirX, _dirY, LastSlope,
            before.X - after.X, before.Y - after.Y,
            report.Err,
            0f,                       // ImpliedLeadMs：组件不再用"领先毫秒"这个口径
            report.ReplayedTicks > 0, // HasStep
            report.ReplayedTicks, 0f, 0f, 0f, 0f, 0f, 0f,
            0f, 0f, 0f,               // Drift*
            0f,                       // K
            _soft, _hard,
            false);
    }

    public void FeedServerMotion(float velX, float velY, float effectiveSpeed, int dirX, int dirY, int pathLen)
    {
        _srvVelX = velX;
        _srvVelY = velY;
        _srvEffectiveSpeed = effectiveSpeed;
        _srvDirX = dirX;
        _srvDirY = dirY;
        _srvPathLen = pathLen;
    }
}
