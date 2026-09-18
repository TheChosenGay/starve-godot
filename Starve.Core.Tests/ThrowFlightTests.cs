using System.Numerics;
using Starve.Core;

namespace Starve.Core.Tests;

/// <summary>
/// 投掷飞行跟踪器（客户端预测 + 权威收敛）的契约。
///
/// 这些测试钉住三件事：
///   1. ghost 的起手延迟与抛物线形状必须与服务端口径一致（复用 ThrowPhysics）；
///   2. ghost → 权威的**交接**不能跳、不能重影；
///   3. 权威 20Hz 采样在 60FPS 下必须被补成连续运动，且本地超前时的回拉有上限
///      （不瞬移回跳），落后时立刻补齐。
/// </summary>
public sealed class ThrowFlightTests
{
    private static readonly Vector2 From = new(10, 10);
    private static readonly Vector2 To = new(14, 10); // 距离 4 → Solve 得 20 tick

    private const double Dt = 1.0 / 60.0;

    private static ThrowFlightSample Only(ThrowFlightTracker tracker)
    {
        var samples = tracker.Samples();
        Assert.Single(samples);
        return samples[0];
    }

    /// <summary>按 60FPS 帧推进指定秒数（最后一帧可能是不足一帧的余量）。</summary>
    private static void TickSeconds(ThrowFlightTracker tracker, double seconds)
    {
        var left = seconds;
        while (left > 1e-12)
        {
            var step = Math.Min(Dt, left);
            tracker.Tick(step);
            left -= step;
        }
    }

    private static void TickFrames(ThrowFlightTracker tracker, int frames)
    {
        for (var i = 0; i < frames; i++) tracker.Tick(Dt);
    }

    /// <summary>起手未满：ghost 悬在起点、高度 0、进度 0（600ms 的"按了没反应"要由预告填掉，而不是乱飞）。</summary>
    [Fact]
    public void GhostDoesNotFlyBeforeWindup()
    {
        var tracker = new ThrowFlightTracker();
        tracker.PredictOwn(From, To);
        Assert.True(tracker.HasOwnGhost);
        Assert.Equal(1, tracker.Count);

        // 起手 12 tick = 0.6s；只走 0.59s
        TickSeconds(tracker, 0.59);

        var sample = Only(tracker);
        Assert.True(sample.IsGhost);
        Assert.Equal(0.0, sample.Progress, 9);
        Assert.Equal(From.X, sample.Position.X, 4);
        Assert.Equal(From.Y, sample.Position.Y, 4);
        Assert.Equal(0.0, sample.Height, 9);
    }

    /// <summary>起手结束才开始飞（余量不丢：跨过起手的那一帧立刻前进）。</summary>
    [Fact]
    public void GhostFliesAfterWindup()
    {
        var tracker = new ThrowFlightTracker();
        tracker.PredictOwn(From, To);

        TickSeconds(tracker, ThrowFlightTracker.ThrowWindupTicks / ThrowPhysics.TicksPerSecond + 1.0 / ThrowPhysics.TicksPerSecond);

        var sample = Only(tracker);
        Assert.True(sample.Progress > 0);
        Assert.True(sample.Height > 0);
        Assert.True(sample.Position.X > From.X); // 朝落点方向前进
    }

    /// <summary>抛物线形状：中途位置 = from→to 线性插值、高度 = ThrowPhysics.HeightAt。</summary>
    [Fact]
    public void ParabolaMatchesThrowPhysics()
    {
        var tracker = new ThrowFlightTracker();
        tracker.PredictOwn(From, To);
        var traj = ThrowPhysics.Solve(From, To);

        // 起手 12 tick + 飞行 5 tick
        TickSeconds(tracker, (ThrowFlightTracker.ThrowWindupTicks + 5) / ThrowPhysics.TicksPerSecond);

        var sample = Only(tracker);
        var expected = traj.SampleAt(5.0);
        Assert.Equal(expected.Pos.X, sample.Position.X, 4);
        Assert.Equal(expected.Pos.Y, sample.Position.Y, 4);
        Assert.Equal(expected.Height, sample.Height, 6);
        Assert.Equal(5.0 / traj.FlightTicks, sample.Progress, 6);
    }

    /// <summary>落地：位置精确落在 To、高度归零、进度 1（复用 SampleAt 的语义）。</summary>
    [Fact]
    public void GhostLandsExactlyOnTarget()
    {
        var tracker = new ThrowFlightTracker();
        tracker.PredictOwn(From, To);
        var traj = ThrowPhysics.Solve(From, To);

        TickSeconds(tracker,
            (ThrowFlightTracker.ThrowWindupTicks + traj.FlightTicks) / ThrowPhysics.TicksPerSecond + 0.05);

        var sample = Only(tracker);
        Assert.Equal(1.0, sample.Progress, 9);
        Assert.Equal(To.X, sample.Position.X, 4);
        Assert.Equal(To.Y, sample.Position.Y, 4);
        Assert.Equal(0.0, sample.Height, 9);
    }

    /// <summary>被拒的投掷不会等到权威：ghost 落地滞留一小段后自行撤掉（本地兜底）。</summary>
    [Fact]
    public void GhostExpiresWhenNeverConfirmed()
    {
        var tracker = new ThrowFlightTracker();
        tracker.PredictOwn(From, To);
        var traj = ThrowPhysics.Solve(From, To);

        TickSeconds(tracker,
            (ThrowFlightTracker.ThrowWindupTicks + traj.FlightTicks) / ThrowPhysics.TicksPerSecond +
            ThrowFlightTracker.GhostLingerSeconds + 0.1);

        Assert.False(tracker.HasOwnGhost);
        Assert.Equal(0, tracker.Count);
        Assert.Empty(tracker.Samples());
    }

    /// <summary>
    /// 和解/交接：权威实体出现后 ghost 消失、权威在飞、交接瞬间位置差 &lt; 0.5 格，
    /// 且不会 ghost 与权威两条同时出现（防重影）。
    /// </summary>
    [Fact]
    public void OwnThrowHandoffHasNoGhostAndNoJump()
    {
        var tracker = new ThrowFlightTracker();
        tracker.PredictOwn(From, To);
        var traj = ThrowPhysics.Solve(From, To);

        // 起手结束 + 飞了 3 tick 的 ghost（真实时序里客户端通常略超前服务端，差 ≈ 一个 RTT）
        TickSeconds(tracker, (ThrowFlightTracker.ThrowWindupTicks + 3) / ThrowPhysics.TicksPerSecond);
        var ghost = Only(tracker);
        Assert.True(ghost.IsGhost);

        // 权威快照到达：elapsed 只有 1（比 ghost 落后 2 tick）
        tracker.Observe(42, From, To, traj.FlightTicks, elapsed: 1,
            gravity: ThrowPhysics.DefaultGravity, ownThrow: true);

        var samples = tracker.Samples();
        Assert.Single(samples);
        Assert.False(samples[0].IsGhost);
        Assert.Equal(42ul, samples[0].EntityId);
        Assert.False(tracker.HasOwnGhost);

        // 交接瞬间位置连续：沿用 ghost 的平滑进度，差异应远小于 1 tick 位移
        Assert.True(
            Vector2.Distance(ghost.Position, samples[0].Position) < 0.5f,
            $"交接位置差 {Vector2.Distance(ghost.Position, samples[0].Position)} 应 < 0.5 格");
    }

    /// <summary>交接后限速收敛到权威（不瞬移），且收敛过程中位置连续前进。</summary>
    [Fact]
    public void OwnThrowHandoffConvergesToAuthority()
    {
        var tracker = new ThrowFlightTracker();
        tracker.PredictOwn(From, To);
        var traj = ThrowPhysics.Solve(From, To);
        TickSeconds(tracker, (ThrowFlightTracker.ThrowWindupTicks + 3) / ThrowPhysics.TicksPerSecond);

        // 权威每 3 帧（20Hz）来一次，elapsed 从 1 开始按真实速率递增
        for (var frame = 0; frame < 30; frame++)
        {
            if (frame % 3 == 0)
            {
                var elapsed = 1 + frame / 3;
                tracker.Observe(42, From, To, traj.FlightTicks, elapsed, ThrowPhysics.DefaultGravity, true);
            }
            tracker.Tick(Dt);
        }

        var sample = Only(tracker);
        // 本地时间与权威外推目标重合：≈ 1 + 10 tick
        Assert.Equal(11.0, sample.Progress * traj.FlightTicks, 1);
    }

    /// <summary>本地落后权威 → 立刻补齐（不允许慢慢追）。</summary>
    [Fact]
    public void LocalBehindAuthorityCatchesUpImmediately()
    {
        var tracker = new ThrowFlightTracker();
        var traj = ThrowPhysics.Solve(From, To);

        tracker.Observe(7, From, To, 200, elapsed: 10, gravity: ThrowPhysics.DefaultGravity, ownThrow: false);
        TickFrames(tracker, 5);
        var before = Only(tracker);

        tracker.Observe(7, From, To, 200, elapsed: 60, gravity: ThrowPhysics.DefaultGravity, ownThrow: false);
        var after = Only(tracker);

        Assert.Equal(60.0, after.Progress * 200, 6);
        Assert.True(after.Position.X > before.Position.X);
    }

    /// <summary>本地远大于权威 → 每帧回拉量有上限（不瞬移回跳）。</summary>
    [Fact]
    public void LocalAheadAuthorityPullsBackWithBoundedRate()
    {
        var tracker = new ThrowFlightTracker();
        var from = new Vector2(0, 0);
        var to = new Vector2(20, 0);
        const int flightTicks = 200;
        var perTick = Vector2.Distance(from, to) / flightTicks;

        // 本地一口气到 100 tick
        tracker.Observe(3, from, to, flightTicks, elapsed: 100, gravity: ThrowPhysics.DefaultGravity, ownThrow: false);
        // 权威回退到 0（例如服务端抖动/存档回读）：本地不得瞬移回去
        tracker.Observe(3, from, to, flightTicks, elapsed: 0, gravity: ThrowPhysics.DefaultGravity, ownThrow: false);
        var before = Only(tracker);
        Assert.Equal(100.0, before.Progress * flightTicks, 6);

        tracker.Tick(Dt); // 一帧（60FPS）
        var after = Only(tracker);
        var moved = Vector2.Distance(before.Position, after.Position);

        // 单帧位移 ≤ 0.5 tick 的位移量（外加极小的浮点余量）
        Assert.True(moved <= perTick * 0.5 + 1e-6, $"单帧回拉 {moved} 超过 0.5 tick 位移 {perTick * 0.5}");
        Assert.True(moved > 0, "超前时应当朝权威方向回拉");
    }

    /// <summary>20Hz 权威采样在 60FPS 下被补成连续运动：没有 1 tick 的整格跳、不倒退。</summary>
    [Fact]
    public void AuthoritySamplesAreSmoothedToFrameRate()
    {
        var tracker = new ThrowFlightTracker();
        var from = new Vector2(0, 0);
        var to = new Vector2(20, 0);
        var traj = ThrowPhysics.Solve(from, to);
        var perTick = Vector2.Distance(from, to) / traj.FlightTicks;

        tracker.Observe(9, from, to, traj.FlightTicks, elapsed: 0, gravity: ThrowPhysics.DefaultGravity, ownThrow: false);
        var prevX = Only(tracker).Position.X;
        var maxStep = 0.0;

        for (var frame = 1; frame <= 60; frame++)
        {
            if (frame % 3 == 0) // 20Hz 快照
            {
                tracker.Observe(9, from, to, traj.FlightTicks, frame / 3, ThrowPhysics.DefaultGravity, ownThrow: false);
            }
            tracker.Tick(Dt);
            var x = Only(tracker).Position.X;
            var step = x - prevX;
            Assert.True(step >= -1e-6, $"第 {frame} 帧位置倒退 {step}");
            maxStep = Math.Max(maxStep, step);
            prevX = x;
        }

        // 单帧位移应约等于 1/3 tick；若退化成"快照才动"，会出现整 1 tick 的跳。
        Assert.True(maxStep < perTick * 0.7, $"单帧最大位移 {maxStep} 接近 1 tick 位移 {perTick}（退化成 20Hz 跳变）");
    }

    /// <summary>非自己投的权威飞行也能被跟踪与采样（别人的炸弹同样要平滑）。</summary>
    [Fact]
    public void TracksRemoteAuthoritativeFlight()
    {
        var tracker = new ThrowFlightTracker();
        var traj = ThrowPhysics.Solve(From, To);

        tracker.Observe(99, From, To, traj.FlightTicks, elapsed: 5,
            gravity: ThrowPhysics.DefaultGravity, ownThrow: false);

        Assert.False(tracker.HasOwnGhost);
        Assert.Equal(1, tracker.Count);
        var sample = Only(tracker);
        Assert.Equal(99ul, sample.EntityId);
        Assert.False(sample.IsGhost);
        var expected = traj.SampleAt(5.0);
        Assert.Equal(expected.Pos.X, sample.Position.X, 4);
        Assert.Equal(expected.Height, sample.Height, 6);
        Assert.Equal(5.0 / traj.FlightTicks, sample.Progress, 6);
    }

    /// <summary>Forget / CancelOwnGhost / Clear 的行为。</summary>
    [Fact]
    public void ForgetCancelAndClearBehave()
    {
        var tracker = new ThrowFlightTracker();
        var traj = ThrowPhysics.Solve(From, To);
        tracker.Observe(5, From, To, traj.FlightTicks, 2, ThrowPhysics.DefaultGravity, false);
        tracker.Observe(6, From, To, traj.FlightTicks, 2, ThrowPhysics.DefaultGravity, false);
        tracker.PredictOwn(From, To);
        Assert.Equal(3, tracker.Count);

        tracker.Forget(5);
        Assert.Equal(2, tracker.Count);
        Assert.DoesNotContain(tracker.Samples(), s => s.EntityId == 5);
        tracker.Forget(5); // 幂等
        Assert.Equal(2, tracker.Count);

        tracker.CancelOwnGhost();
        Assert.False(tracker.HasOwnGhost);
        Assert.Equal(1, tracker.Count);

        tracker.Clear();
        Assert.Equal(0, tracker.Count);
        Assert.Empty(tracker.Samples());
    }

    /// <summary>ghost 重复预测只保留最新一条（同一时刻只预告一次投掷）。</summary>
    [Fact]
    public void PredictOwnReplacesPreviousGhost()
    {
        var tracker = new ThrowFlightTracker();
        tracker.PredictOwn(From, To);
        tracker.PredictOwn(new Vector2(0, 0), new Vector2(6, 0));
        Assert.Equal(1, tracker.Count);
        Assert.True(tracker.HasOwnGhost);
    }

    /// <summary>边界：flightTicks &lt;= 0、gravity &lt;= 0、距离为 0 都不崩。</summary>
    [Fact]
    public void HandlesDegenerateInputs()
    {
        var tracker = new ThrowFlightTracker();

        // flightTicks = 0：视为立即落地（SampleAt 语义），进度 1
        tracker.Observe(1, From, From, 0, 0, ThrowPhysics.DefaultGravity, false);
        var zero = Only(tracker);
        Assert.Equal(1.0, zero.Progress, 9);
        Assert.Equal(From.X, zero.Position.X, 4);
        Assert.Equal(0.0, zero.Height, 9);
        TickFrames(tracker, 10);
        Assert.Equal(1, tracker.Count);

        // gravity <= 0：回退到默认重力，高度不为 NaN
        tracker.Forget(1);
        tracker.Observe(2, From, To, 20, 5, 0, false);
        var badGravity = Only(tracker);
        Assert.False(double.IsNaN(badGravity.Height));
        Assert.True(badGravity.Height > 0);

        // 距离 0 的本地预测：FlightTicks 至少 1，落地不崩
        tracker.Clear();
        tracker.PredictOwn(From, From);
        TickSeconds(tracker, 2.0);
        var degenerateGhost = Only(tracker);
        Assert.Equal(From.X, degenerateGhost.Position.X, 4);
        Assert.Equal(0.0, degenerateGhost.Height, 9);
    }

    /// <summary>
    /// 渲染层的消费契约：<see cref="ThrowFlightTracker.TryGet"/> 只按**权威实体 id** 取样本。
    ///
    /// EntityLayer3D 与 GameRoot 共用同一条 tracker（ghost 与权威一份进度，交接不跳），
    /// 渲染时按 id 查；ghost 的保留 id 0 与未知 id 都必须返回 false，
    /// 否则会把 ghost 画到某个实体节点上、或对着不存在的实体取值。
    /// </summary>
    [Fact]
    public void TryGetServesOnlyAuthoritativeFlights()
    {
        var tracker = new ThrowFlightTracker();
        var traj = ThrowPhysics.Solve(From, To);
        tracker.PredictOwn(From, To);
        tracker.Observe(7, From, To, traj.FlightTicks, 3, ThrowPhysics.DefaultGravity, false);

        Assert.True(tracker.TryGet(7, out var authoritative));
        Assert.False(authoritative.IsGhost);
        Assert.Equal(7UL, authoritative.EntityId);
        Assert.Equal(ThrowPhysics.Solve(From, To).FlightTicks, traj.FlightTicks);

        // 与 Samples() 里同一条飞行必须给出**同一个**位置（两份状态不一致就是交接跳变的来源）。
        var sample = tracker.Samples().Single(s => s.EntityId == 7);
        Assert.Equal(sample.Position.X, authoritative.Position.X, 6);
        Assert.Equal(sample.Position.Y, authoritative.Position.Y, 6);
        Assert.Equal(sample.Height, authoritative.Height, 9);

        // ghost（id 0）与未知 id 都不该被当成权威飞行。
        Assert.False(tracker.TryGet(0, out _));
        Assert.False(tracker.TryGet(999, out _));
    }
}
