using System;
using System.Collections.Generic;
using System.Numerics;

namespace Starve.Core;

/// <summary>
/// 一条投掷飞行的渲染快照。渲染层只消费这个结构，不碰协议/网络细节。
/// </summary>
/// <param name="EntityId">权威实体 id；本地预测的 ghost 没有实体，固定为 0。</param>
/// <param name="IsGhost">true = 本地预测（尚未被服务端确认）；false = 服务端权威飞行。</param>
/// <param name="Position">水平位置（格坐标）。</param>
/// <param name="Height">离地高度（格）。渲染时叠加到屏幕 Y 上。</param>
/// <param name="Progress">飞行进度 0..1；落地精确为 1。</param>
public readonly record struct ThrowFlightSample(
    ulong EntityId,
    bool IsGhost,
    Vector2 Position,
    double Height,
    double Progress);

/// <summary>
/// 投掷飞行跟踪器：本地预测 ghost + 权威飞行，并把 20Hz 的权威采样补成逐帧平滑。
///
/// 为什么需要它：
///   - 服务端把投掷当**动作**处理（起手 12 tick → Commit 才挂 Thrown 开始飞），
///     客户端若只等 Thrown 会出现 600ms 的"按了没反应"；
///   - 挂上 Thrown 后服务端只做**水平**插值、每 tick 标脏下发，高度完全由客户端还原；
///   - 快照是 20Hz（拥塞时 10Hz），而渲染 60FPS，直接把 elapsed 截成整数会"一格一顿"。
///
/// 设计取舍：
///   - **ghost 只是预告**。它用与服务端同一套公式（<see cref="ThrowPhysics"/>）算轨迹，
///     起手结束后开始飞；一旦权威实体出现就**交接**（移除 ghost、权威接管），
///     交接时沿用 ghost 的平滑进度再限速收敛，所以画面不会跳。
///   - **权威采样要"外推到现在"**。快照里的 elapsed 是**下发那一刻**的值，
///     拿它跟当前帧的本地时间直接比，等价于让本地时间每份快照往前跳一格（20Hz 一顿）。
///     因此 Tick 会把权威目标按同一速率一起推进（即"权威此刻应该飞到的 tick"），
///     再比较二者：本地落后就补齐（服务端已飞到前面），本地超前就限速回拉（不瞬移回跳）。
///   - 被拒的投掷不会再有权威实体。ghost 落地后滞留一小段时间等权威，
///     超时即自行撤掉（纯本地兜底，不依赖 outcome 管线）。
///
/// 纯逻辑：无 Godot 依赖，只用 System.Numerics，时间由外部按帧注入。
/// </summary>
public sealed class ThrowFlightTracker
{
    /// <summary>
    /// 起手 tick 数——**镜像服务端常量**（服务端 <c>ThrowWindupTicks = 12</c>，20Hz ⇒ 600ms）。
    ///
    /// 客户端不可能提前知道服务端改没改这个值，所以它只影响"本地 ghost 早/晚多久起飞"，
    /// 不影响任何判定：真正的飞行进度始终以快照里的 <c>Thrown.elapsed</c> 为准。
    /// 两边不一致时最多表现为 ghost 与权威差几 tick，交接时按限速收敛掉。
    /// </summary>
    public const int ThrowWindupTicks = 12;

    /// <summary>
    /// 本地超前权威时的回拉速度（tick/秒）。30 = 60FPS 下每帧最多 0.5 tick。
    ///
    /// 为什么限速而不是直接对齐：直接对齐 = 瞬移回跳，肉眼可见"橡皮筋"。
    /// 按帧摊开则是一次几十毫秒的短过渡，且帧率无关。
    /// </summary>
    public const double MaxReconcileTicksPerSecond = 30.0;

    /// <summary>
    /// ghost 落地后继续保留的秒数（等待权威接管）。
    /// 超时仍没等到说明投掷被服务端拒绝（或丢失），此时撤掉预告，避免地面留下"假炸弹"。
    /// </summary>
    public const double GhostLingerSeconds = 1.5;

    /// <summary>一条飞行（ghost 或权威）的内部状态。</summary>
    private sealed class Flight
    {
        public ulong EntityId;
        public bool IsGhost;
        public bool OwnThrow;
        public ThrowTrajectory Traj;

        /// <summary>本地平滑推进的已飞 tick（double：帧间连续）。</summary>
        public double Elapsed;

        /// <summary>
        /// 最近一次权威 elapsed，并按帧一起推进（见 <see cref="Tick"/>）：
        /// 语义是"权威轨迹**此刻**应该飞到的 tick"，不是快照原始值。
        /// </summary>
        public double Target;
        public bool HasTarget;

        /// <summary>ghost 专用：起手剩余 tick。</summary>
        public double WindupLeft;

        /// <summary>ghost 专用：落地后滞留时长（秒）。</summary>
        public double Linger;

        public ThrowFlightSample Sample()
        {
            var (pos, height) = Traj.SampleAt(Elapsed);
            var progress = Traj.FlightTicks <= 0 ? 1.0 : Math.Clamp(Elapsed / Traj.FlightTicks, 0, 1);
            return new ThrowFlightSample(EntityId, IsGhost, pos, height, progress);
        }
    }

    // 飞行数量极小（自己一颗 + 视野内几颗），用 List 线性查找即可：
    // 顺序稳定（ghost 永远在 0 号）让 Samples() 的结果可预期，也省掉字典分配。
    private readonly List<Flight> _flights = new();
    private readonly List<ThrowFlightSample> _samples = new();

    /// <summary>是否存在本地预测的 ghost（测试/调试用）。</summary>
    public bool HasOwnGhost { get; private set; }

    /// <summary>当前跟踪的飞行条数（ghost + 权威）。</summary>
    public int Count => _flights.Count;

    /// <summary>
    /// 记录一次**自己发起**的投掷，开始本地 ghost 预测。
    ///
    /// from/to 由调用方按发令瞬间的本地位置/瞄准点给出；轨迹用与服务端同一公式求解。
    /// 起手 <paramref name="windupTicks"/> 之后 ghost 才离手（与服务端 Commit 对齐）。
    /// 重复调用会替换上一条 ghost（同一时刻只预告一次投掷）。
    /// </summary>
    public void PredictOwn(
        Vector2 from,
        Vector2 to,
        double gravity = ThrowPhysics.DefaultGravity,
        int windupTicks = ThrowWindupTicks)
    {
        CancelOwnGhost();
        var flight = new Flight
        {
            EntityId = 0,
            IsGhost = true,
            OwnThrow = true,
            Traj = ThrowPhysics.Solve(from, to, gravity),
            WindupLeft = Math.Max(0, windupTicks),
        };
        // ghost 固定在列表头：Samples() 顺序稳定（渲染层先拿 ghost）。
        _flights.Insert(0, flight);
        HasOwnGhost = true;
    }

    /// <summary>
    /// 观察一次权威飞行（快照里带 <c>Thrown</c> 的实体）。同一实体重复调用 = 更新。
    ///
    /// ownThrow 且存在 ghost 时做**交接**：ghost 被权威接管，且沿用 ghost 的平滑进度，
    /// 保证交接瞬间画面位置连续（两者用同一公式，进度差通常 &lt; 1 tick）。
    /// </summary>
    public void Observe(
        ulong entityId,
        Vector2 from,
        Vector2 to,
        int flightTicks,
        int elapsed,
        double gravity,
        bool ownThrow)
    {
        if (entityId == 0) return; // 0 是 ghost 的保留 id，权威实体 id 不会是 0
        if (gravity <= 0) gravity = ThrowPhysics.DefaultGravity;
        if (flightTicks < 0) flightTicks = 0;

        var traj = new ThrowTrajectory
        {
            From = from,
            To = to,
            FlightTicks = flightTicks,
            PeakHeight = ThrowPhysics.PeakHeight(flightTicks, gravity),
            Gravity = gravity,
        };
        var target = Math.Clamp((double)elapsed, 0, flightTicks);

        var handoff = false;
        var flight = Find(entityId);
        if (flight is null)
        {
            // 新建：正常从权威 elapsed 开始；若这是自己的投掷且正在预告，则从 ghost 的
            // 进度接管（交接不跳），只有当 ghost 落后权威时才立刻补齐。
            var start = target;
            if (ownThrow && TryGetGhost(out var ghost))
            {
                start = Math.Max(ghost.Elapsed, target);
                handoff = true;
            }
            flight = new Flight
            {
                EntityId = entityId,
                IsGhost = false,
                OwnThrow = ownThrow,
                Traj = traj,
                Elapsed = Math.Clamp(start, 0, flightTicks),
            };
            _flights.Add(flight);
        }
        else
        {
            // 已跟踪：轨迹以权威为准（存档恢复等情况下 from/to 可能变），
            // 本地落后立刻补齐；超前留给 Tick 限速回拉。
            flight.Traj = traj;
            flight.OwnThrow = ownThrow;
            flight.Elapsed = Math.Clamp(flight.Elapsed, 0, flightTicks);
            if (flight.Elapsed < target) flight.Elapsed = target;
        }

        flight.Target = target;
        flight.HasTarget = true;

        // 交接完成：撤掉 ghost，避免画面同时出现"预告弹 + 权威弹"两条（重影）。
        if (handoff) CancelOwnGhost();
    }

    /// <summary>实体消失或 <c>Thrown</c> 被移除（落地）时调用。</summary>
    public void Forget(ulong entityId)
    {
        if (entityId == 0) return;
        for (var i = 0; i < _flights.Count; i++)
        {
            if (_flights[i].EntityId != entityId) continue;
            _flights.RemoveAt(i);
            return;
        }
    }

    /// <summary>投掷被拒/失败时撤掉本地预告（画面立刻收回那颗"还没扔出去的炸弹"）。</summary>
    public void CancelOwnGhost()
    {
        for (var i = 0; i < _flights.Count; i++)
        {
            if (!_flights[i].IsGhost) continue;
            _flights.RemoveAt(i);
            break;
        }
        HasOwnGhost = false;
    }

    /// <summary>清空全部（断线/重连等）。</summary>
    public void Clear()
    {
        _flights.Clear();
        HasOwnGhost = false;
    }

    /// <summary>
    /// 按帧推进本地时间（秒）。
    ///
    /// ghost：先走完起手，再把本帧剩余时间折算成飞行 tick（起手跨帧时余量不丢）。
    /// 权威：直接按 dt 推进；本地超前目标时按 <see cref="MaxReconcileTicksPerSecond"/> 限速回拉。
    /// </summary>
    public void Tick(double dtSeconds)
    {
        if (dtSeconds <= 0) return;
        var stepTicks = dtSeconds * ThrowPhysics.TicksPerSecond;
        var maxPullback = MaxReconcileTicksPerSecond * dtSeconds;

        for (var i = _flights.Count - 1; i >= 0; i--)
        {
            var f = _flights[i];

            // 起手阶段：ghost 悬在起点不动；跨过起手的那一帧把余量补进飞行时间。
            var advance = stepTicks;
            if (f.IsGhost && f.WindupLeft > 0)
            {
                if (advance < f.WindupLeft)
                {
                    f.WindupLeft -= advance;
                    advance = 0;
                }
                else
                {
                    advance -= f.WindupLeft;
                    f.WindupLeft = 0;
                }
            }
            f.Elapsed += advance;

            // 权威目标同步外推：快照之间权威也在飞（服务端每 tick elapsed++），
            // 把目标按同一速率推进到"此刻"，本地才能跟上而不是等下一份快照才跳一格。
            if (f.HasTarget)
            {
                f.Target = Math.Min(f.Traj.FlightTicks, f.Target + advance);
                if (f.Elapsed < f.Target)
                {
                    // 落后 → 立刻补齐（服务端已经飞到前面，慢慢追只会让误差一直在）
                    f.Elapsed = f.Target;
                }
                else if (f.Elapsed > f.Target)
                {
                    // 超前 → 每帧最多回拉一个小量，绝不瞬移回跳
                    f.Elapsed = Math.Max(f.Target, f.Elapsed - maxPullback);
                }
            }

            if (f.Elapsed < 0) f.Elapsed = 0;
            if (f.Elapsed > f.Traj.FlightTicks) f.Elapsed = f.Traj.FlightTicks;

            // ghost 落地后滞留超时 → 视作投掷被拒，自行撤掉（本地兜底，不依赖 outcome）。
            if (f.IsGhost && f.Elapsed >= f.Traj.FlightTicks)
            {
                f.Linger += dtSeconds;
                if (f.Linger > GhostLingerSeconds)
                {
                    _flights.RemoveAt(i);
                    HasOwnGhost = false;
                }
            }
        }
    }

    /// <summary>
    /// 当前所有飞行的渲染快照。
    ///
    /// ⚠️ 返回的列表由内部**复用**（每帧重建，避免 60FPS 下的分配）：调用方应当帧立刻消费，
    /// 不要长期持有或跨帧比较。ghost（若有）排在第一条。
    /// </summary>
    public IReadOnlyList<ThrowFlightSample> Samples()
    {
        _samples.Clear();
        foreach (var f in _flights) _samples.Add(f.Sample());
        return _samples;
    }

    /// <summary>
    /// 按实体 id 取一条权威飞行的当前样本（渲染层逐实体查用）。
    ///
    /// ghost（<c>EntityId == 0</c>）不走这里：它由专属表现层消费 <see cref="Samples"/>。
    /// 未知 id 返回 false，调用方按普通实体渲染即可。
    /// </summary>
    public bool TryGet(ulong entityId, out ThrowFlightSample sample)
    {
        if (entityId != 0)
        {
            foreach (var f in _flights)
            {
                if (f.EntityId != entityId) continue;
                sample = f.Sample();
                return true;
            }
        }
        sample = default;
        return false;
    }

    private Flight? Find(ulong entityId)
    {
        foreach (var f in _flights)
        {
            if (f.EntityId == entityId) return f;
        }
        return null;
    }

    private bool TryGetGhost(out Flight ghost)
    {
        foreach (var f in _flights)
        {
            if (!f.IsGhost) continue;
            ghost = f;
            return true;
        }
        ghost = null!;
        return false;
    }
}
