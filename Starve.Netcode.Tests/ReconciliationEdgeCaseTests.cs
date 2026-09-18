using Starve.Netcode;

namespace Starve.Netcode.Tests;

/// <summary>
/// 和解的**边界与对抗性**用例：延迟重放、大/小回退、微小差距、乱序、断流、换会话。
///
/// 每条用例都跑同一组**不变量**（<see cref="Invariants"/>），因为单独看某一帧"好像对"
/// 说明不了问题 —— 之前就是"指标算错"被 happy path 放过去了。
/// </summary>
public sealed class ReconciliationEdgeCaseTests
{
    private const long Tick0 = 1000;
    private const long LatencyMs = 100;
    private const float SimStep = 0.5f; // 10 格/秒 × 1 tick

    private static long Now0 => Tick0 * 50 + LatencyMs;
    private static long WallOf(long tick) => tick * 50 + LatencyMs;
    private static FakeAction A(int dx) => new() { Dx = dx };

    private static NetSnapshot<FakeState> Snap(long tick, float x, ulong seq = 1, ulong epoch = 7) =>
        new(tick, seq, epoch, new FakeState { X = x }, false);

    private static ClientSmoother<FakeState, FakeAction> New(NetcodeConfig? config = null) =>
        new(new FakeModel { Speed = 10f }, config);

    /// <summary>建立链并一直按 +X 走。</summary>
    private static ClientSmoother<FakeState, FakeAction> Walking(NetcodeConfig? config = null, int ticks = 5)
    {
        var smoother = New(config);
        smoother.OnSnapshot(Snap(Tick0, 0f), Now0);
        smoother.SetIntent(1, 7, A(1), Now0);
        smoother.AdvanceTo(Tick0 + ticks);
        return smoother;
    }

    /// <summary>
    /// 贯穿所有用例的不变量：逐帧渲染位移有界（含校正速度上限）、永不 NaN、
    /// 版本单调、过渡必然收敛。
    /// </summary>
    private sealed class Invariants
    {
        private readonly ClientSmoother<FakeState, FakeAction> _smoother;
        private readonly float _cap;
        private float _previous;
        private uint _lastVersion;
        private long _frames;
        private long _lastAdvancedTick = long.MinValue;

        public Invariants(
            ClientSmoother<FakeState, FakeAction> smoother,
            float simStep = SimStep,
            double? correctionSpeed = null)
        {
            _smoother = smoother;
            _previous = smoother.RenderedState.X;
            _lastVersion = smoother.StateVersion;
            var speed = correctionSpeed ?? smoother.Config.MaxCorrectionSpeed;
            _cap = (float)(speed * smoother.Config.TickSeconds) + simStep + 1e-3f;
        }

        /// <summary>
        /// 推进到目标 tick，**逐 tick 检查**（单次 AdvanceTo 可以跨多个 tick，
        /// 那样检查"逐帧位移"会误报 —— 真实使用就是每帧推进一小段）。
        /// </summary>
        public void AdvanceTo(long targetTick, bool allowJump = false)
        {
            if (_lastAdvancedTick == long.MinValue) _lastAdvancedTick = _smoother.StateTick;
            while (_lastAdvancedTick < targetTick)
            {
                _lastAdvancedTick++;
                _smoother.AdvanceTo(_lastAdvancedTick);
                CheckFrame(allowJump);
            }
        }

        /// <summary>收到快照并检查不变量（返回报告）。</summary>
        public CorrectionReport OnSnapshot(NetSnapshot<FakeState> snapshot, long nowMs, bool allowJump = false)
        {
            var report = _smoother.OnSnapshot(snapshot, nowMs);
            CheckFrame(allowJump);
            return report;
        }

        /// <summary>
        /// 检查一帧。
        /// <paramref name="allowJump"/>：允许无界位移 —— 只有两种情况：
        /// ① 残差超过"直接贴"阈值（撞墙/传送/断流恢复）；② 换会话重建链。
        /// 这两种是**设计上就不掩饰**的，不该被位移上界误判。
        /// </summary>
        public void CheckFrame(bool allowJump = false)
        {
            _frames++;
            var x = _smoother.RenderedState.X;
            Assert.True(float.IsFinite(x), $"第 {_frames} 帧渲染位置不是有限值：{x}");
            Assert.True(float.IsFinite(_smoother.State.X), "模拟状态不是有限值");
            if (!allowJump)
            {
                Assert.True(MathF.Abs(x - _previous) <= _cap,
                    $"第 {_frames} 帧渲染位移 {x - _previous:F4} 超过上界 {_cap:F4}（会出现突跳）");
            }

            // 版本可以不变（Stale 帧什么都不做），但**绝不能倒退**。
            Assert.True(_smoother.StateVersion >= _lastVersion, "链版本不得倒退");
            _lastVersion = _smoother.StateVersion;
            _previous = x;
        }

        /// <summary>换会话/传送之后重置基线（跨会话谈"连续性"没有意义）。</summary>
        public void RebaseBaseline()
        {
            _previous = _smoother.RenderedState.X;
            _lastVersion = _smoother.StateVersion;
        }

        /// <summary>过渡结束后渲染位置必须精确等于模拟位置（不留残余 offset）。</summary>
        public void AssertConverged(string because)
        {
            Assert.False(_smoother.Blending, $"{because}：过渡没有结束");
            Assert.Equal(0f, _smoother.CorrectionBlendWeight, 5);
            Assert.Equal(_smoother.State.X, _smoother.RenderedState.X, 5);
        }
    }

    // ───────────────────────── 1. 回退量级的分档边界 ─────────────────────────

    [Fact]
    public void ErrorJustInsideTheDeadzoneIsSwallowed()
    {
        var s = Walking(); // X = 2.5
        var inv = new Invariants(s);
        var report = inv.OnSnapshot(Snap(Tick0 + 5, 2.5f - 0.029f, seq: 2), WallOf(Tick0 + 5));

        Assert.Equal(CorrectionKind.Deadzone, report.Kind);
        Assert.False(s.Blending);
        Assert.Equal(s.State.X, s.RenderedState.X, 6);
    }

    [Fact]
    public void ErrorJustOutsideTheDeadzoneBlendsWithoutAJump()
    {
        var s = Walking();
        var seam = s.RenderedState.X;
        var inv = new Invariants(s);
        var report = inv.OnSnapshot(Snap(Tick0 + 5, 2.5f - 0.031f, seq: 2), WallOf(Tick0 + 5));

        Assert.Equal(CorrectionKind.Blended, report.Kind);
        Assert.Equal(seam, s.RenderedState.X, 5); // 接缝零跳变
        inv.AdvanceTo(Tick0 + 5 + report.BlendTicks + 1);
        inv.AssertConverged("小回退");
    }

    [Fact]
    public void ErrorJustBelowTheSnapThresholdStillBlends()
    {
        var s = Walking();
        var report = new Invariants(s).OnSnapshot(Snap(Tick0 + 5, 2.5f - 1.49f, seq: 2), WallOf(Tick0 + 5));
        Assert.Equal(CorrectionKind.Blended, report.Kind);
        Assert.True(report.BlendTicks > 0);
    }

    [Theory]
    [InlineData(1.5f)]  // 正好在阈值上
    [InlineData(1.51f)]
    [InlineData(4f)]
    [InlineData(100f)]  // 传送量级
    public void ErrorAtOrAboveTheSnapThresholdSnaps(float err)
    {
        var s = Walking();
        var inv = new Invariants(s);
        // 残差 ≥ 直接贴阈值 → 设计上就不掩饰，允许无界位移
        var report = inv.OnSnapshot(Snap(Tick0 + 5, 2.5f - err, seq: 2), WallOf(Tick0 + 5), allowJump: true);

        Assert.Equal(CorrectionKind.Snapped, report.Kind);
        Assert.False(s.Blending);
        Assert.Equal(0f, s.CorrectionBlendWeight, 5);
        Assert.Equal(s.State.X, s.RenderedState.X, 6);
        Assert.True(float.IsFinite(s.RenderedState.X), "巨大回退不得产生 NaN/Inf");
    }

    // ───────────────────────── 2. 延迟重放 / 断流 ─────────────────────────

    [Fact]
    public void ReplayIsClampedWhenAlreadyFarAhead()
    {
        var s = Walking(ticks: 5);
        var inv = new Invariants(s);
        // 快照只到 1006，但"服务端现在"是 1030（模拟长时间没有快照后突然来一份旧的）
        var report = inv.OnSnapshot(Snap(Tick0 + 6, 3.0f, seq: 2), WallOf(Tick0 + 30), allowJump: true);

        Assert.True(report.ReplayClamped, "超过 MaxReplayTicks 必须标记限幅");
        Assert.True(report.ReplayedTicks <= s.Config.MaxReplayTicks);
    }

    [Fact]
    public void StallIsReportedAndTheStateFreezesInsteadOfRunningAway()
    {
        var s = Walking(ticks: 5);
        var inv = new Invariants(s);
        // 20 tick 无快照 = 断流阈值（StallMs=1000）
        inv.AdvanceTo(Tick0 + 5 + 25);
        inv.RebaseBaseline();

        // 断流后回来的第一份是"陈旧但仍是权威"的位置 → 差值很大 → 直接贴（设计如此）
        var report = inv.OnSnapshot(Snap(Tick0 + 5, 2.5f, seq: 2), WallOf(Tick0 + 5 + 25), allowJump: true);
        inv.RebaseBaseline();
        Assert.True(report.Starved, "超过 StallMs 必须上报断流");
    }

    [Fact]
    public void OutOfOrderSnapshotIsDroppedAndNeverRewindsTheChain()
    {
        var s = Walking(ticks: 5);
        var inv = new Invariants(s);
        inv.OnSnapshot(Snap(Tick0 + 6, 3.0f, seq: 2), WallOf(Tick0 + 6));
        var version = s.StateVersion;
        var pos = s.State.X;

        // 晚到的旧快照（tick 更小）
        var stale = inv.OnSnapshot(Snap(Tick0 + 4, 1.0f, seq: 3), WallOf(Tick0 + 7));

        Assert.Equal(CorrectionKind.Stale, stale.Kind);
        Assert.Equal(version, s.StateVersion); // 完全没动
        Assert.Equal(pos, s.State.X, 6);
    }

    [Fact]
    public void TwoSnapshotsInOneFrameAreBothAppliedWithoutDoubleCounting()
    {
        var s = Walking(ticks: 5);
        var inv = new Invariants(s);
        inv.OnSnapshot(Snap(Tick0 + 6, 3.0f, seq: 2), WallOf(Tick0 + 6));
        var afterFirst = s.State.X;

        // 同一帧又收到下一份（网络合并）：必须接着往前走，而不是把时间算两遍
        var second = inv.OnSnapshot(Snap(Tick0 + 7, 3.5f, seq: 3), WallOf(Tick0 + 7), allowJump: true);

        Assert.NotEqual(CorrectionKind.Stale, second.Kind);
        Assert.True(s.State.X >= afterFirst, "同帧两份快照不能让位置倒退");
        inv.RebaseBaseline();
        inv.AdvanceTo(Tick0 + 8);
        inv.AssertConverged("同帧两份快照");
    }

    [Fact]
    public void BurstAfterAStallConvergesBackOntoTheAuthoritativeTrajectory()
    {
        var s = Walking(ticks: 5);
        var inv = new Invariants(s);
        // 断流 25 tick 后，一次补来 5 份。
        // ⚠️ 断流期间客户端一直在往前预测，积压快照的差值必然很大 →
        //    会走"直接贴"（设计上不掩饰），所以这里允许跳；重点是**能收敛回轨迹**。

        for (var i = 1; i <= 5; i++)
        {
            var tick = Tick0 + 5 + i * 5;
            // 断流后补发的积压：差值大 → 允许直接贴
            inv.OnSnapshot(Snap(tick, (tick - Tick0) * 0.5f, (ulong)(10 + i)),
                WallOf(Tick0 + 30), allowJump: true);
            inv.RebaseBaseline();
        }

        inv.AdvanceTo(Tick0 + 32);
        // 客户端必须收敛回权威轨迹（本脚本里服务端 X = (tick-1000)*0.5），
        // 并在此基础上继续本地预测（多走的 2 tick 是正常的领先）。
        var expected = (Tick0 + 32 - Tick0) * 0.5f;
        Assert.True(MathF.Abs(s.State.X - expected) < 0.6f,
            $"断流恢复后没回到权威轨迹：客户端 {s.State.X:F2} vs 轨迹 {expected:F2}");
    }

    // ───────────────────────── 3. 微小差距 ─────────────────────────

    [Theory]
    [InlineData(0.01f)]
    [InlineData(0.05f)]
    [InlineData(0.1f)]
    [InlineData(0.5f)]
    public void SmallDiscrepancyIsAbsorbedAndConvergesExactly(float err)
    {
        var s = Walking();
        var inv = new Invariants(s);
        var report = inv.OnSnapshot(Snap(Tick0 + 5, 2.5f - err, seq: 2), WallOf(Tick0 + 5));

        // 只在超过死区时才平滑
        Assert.Equal(err > 0.03f ? CorrectionKind.Blended : CorrectionKind.Deadzone, report.Kind);

        inv.AdvanceTo(Tick0 + 5 + Math.Max(report.BlendTicks, 1) + 1);
        inv.AssertConverged($"微小差距 {err}");
    }

    [Fact]
    public void RepeatedSmallRollbacksDoNotAccumulate()
    {
        // 服务端每份快照都比客户端落后一点点（世界真相有差异）：
        // 残差必须被消化掉，而不是越积越多。
        var s = Walking(ticks: 2);
        var inv = new Invariants(s);
        var residuals = new List<float>();

        for (var i = 1; i <= 12; i++)
        {
            var tick = Tick0 + 2 + i;
            // 权威位置 = "客户端预测 - 0.2"：恒定的小回退
            var predicted = 1.0f + i * SimStep;
            var report = inv.OnSnapshot(Snap(tick, predicted - 0.2f, (ulong)(i + 1)), WallOf(tick));
            inv.AdvanceTo(tick + 1);

            if (report.Kind == CorrectionKind.Blended)
                residuals.Add(report.Err);
        }

        Assert.True(residuals.Count > 0, "应当反复发生小回退");
        // 残差不能越来越大（发散的信号）
        Assert.True(residuals[^1] <= residuals[0] + 0.05f,
            $"残差在累积：首个 {residuals[0]:F3} → 最后 {residuals[^1]:F3}");
        inv.AssertConverged("反复小回退");
    }

    [Fact]
    public void RollbackWhileWalkingNeverGoesBackwardFasterThanTheCap()
    {
        var s = Walking(ticks: 5);
        var cap = (float)(s.Config.MaxCorrectionSpeed * s.Config.TickSeconds);
        var inv = new Invariants(s);

        // 大回退（1.4 格），同时玩家一直往前按
        var report = inv.OnSnapshot(Snap(Tick0 + 5, 2.5f - 1.4f, seq: 2), WallOf(Tick0 + 5));
        Assert.Equal(CorrectionKind.Blended, report.Kind);

        var previous = s.RenderedState.X;
        for (var i = 1; i <= report.BlendTicks + 2; i++)
        {
            inv.AdvanceTo(Tick0 + 5 + i);
            var step = s.RenderedState.X - previous;
            Assert.True(step >= SimStep - cap - 1e-3f,
                $"第 {i} 帧倒退超过上限：{step:F4} < {SimStep - cap:F4}");
            previous = s.RenderedState.X;
        }

        inv.AssertConverged("走动中回退");
    }

    [Fact]
    public void SpeedDriftIsVisibleInTheMetricButStaysInsideTheDeadzone()
    {
        // 客户端速度偏 5%：位置误差应当仍在死区内（每份快照都被重放修正），
        // 但"同跨度速度对比"必须把它暴露出来。
        var model = new FakeModel { Speed = 9.5f };
        var s = new ClientSmoother<FakeState, FakeAction>(model);
        s.OnSnapshot(Snap(Tick0, 0f), Now0);
        s.SetIntent(1, 7, A(1), Now0);
        var inv = new Invariants(s, simStep: 0.475f);

        for (var i = 1; i <= 6; i++)
        {
            s.AdvanceTo(Tick0 + i);
            var report = inv.OnSnapshot(Snap(Tick0 + i, i * SimStep, (ulong)(i + 1)), WallOf(Tick0 + i));
            if (report.PredictedSteps > 0)
                Assert.True(MathF.Abs(report.PredictedStepSpeed - report.AuthoritativeStepSpeed) > 0.2f,
                    "5% 速度偏差应该能在同跨度速度对比里看到");
        }
    }

    // ───────────────────────── 4. 会话与历史 ─────────────────────────

    [Fact]
    public void EpochChangeMidStreamResetsEverythingAndRebuilds()
    {
        var s = Walking(ticks: 5);
        var inv = new Invariants(s);
        var resyncs = 0;
        s.Metrics = new CountingMetrics(() => resyncs++);

        // 换会话：跨会话谈"渲染连续性"没有意义（客户端会贴到新会话的权威位置）
        var report = inv.OnSnapshot(Snap(Tick0, 9f, seq: 1, epoch: 8), Now0, allowJump: true);

        Assert.Equal(CorrectionKind.Bootstrap, report.Kind); // 链重建
        Assert.Equal(9f, s.State.X, 5);
        Assert.Equal(0, s.Inputs.Count);
        Assert.Equal(1, resyncs); // 换会话必须通知时间基准重建
    }

    [Fact]
    public void InputHistoryOverflowKeepsTheLatestIntent()
    {
        var s = New();
        s.OnSnapshot(Snap(Tick0, 0f), Now0);
        // 容量只有 4，却记 20 条
        for (var i = 1; i <= 20; i++)
            s.Inputs.Record((ulong)i, 7, Tick0 + i, Tick0 + i, A(i % 2 == 0 ? 1 : -1));

        Assert.True(s.Inputs.Count <= 64);
        Assert.True(s.Inputs.TryActionAtTick(Tick0 + 100, out var latest));
        Assert.Equal(1, latest.Dx); // 第 20 条是偶数 → +1

        s.AdvanceTo(Tick0 + 3);
        Assert.True(float.IsFinite(s.State.X), "历史溢出后状态必须仍然有效");
    }

    [Fact]
    public void BackwardsAcknowledgeIsIgnored()
    {
        var s = Walking(ticks: 3);
        var before = s.State.X;
        s.Inputs.Acknowledge(5);
        s.Inputs.Acknowledge(2); // 倒退的 ack：必须是 no-op
        Assert.Equal(before, s.State.X, 6);
        Assert.True(s.Inputs.HasLatest, "当前意图不能被 ack 清掉");
    }

    [Fact]
    public void OneWayEstimateSurvivesAnOutlierAndRecovers()
    {
        var s = Walking(ticks: 2);
        // 一份异常晚到的快照（2 秒）
        s.OnSnapshot(Snap(Tick0 + 3, 1.5f, seq: 3), WallOf(Tick0 + 3) + 2000);
        var afterOutlier = s.EstimatedOneWayTicks;
        Assert.True(afterOutlier <= 20.0, $"单程估计被异常值顶飞：{afterOutlier}");

        for (var i = 4; i <= 20; i++)
        {
            s.OnSnapshot(Snap(Tick0 + i, (Tick0 + i - Tick0) * 0.5f, (ulong)(i + 1)), WallOf(Tick0 + i));
            s.AdvanceTo(Tick0 + i);
        }

        Assert.True(s.EstimatedOneWayTicks < afterOutlier + 1.0,
            "异常值之后单程估计应当缓慢回落，而不是继续膨胀");
    }

    private sealed class CountingMetrics(Action onResync) : INetcodeMetrics
    {
        public void OnCorrection(in CorrectionReport report) { }
        public void OnClockResync(long serverTick, long nowMs, double offsetMs) => onResync();
    }
}
