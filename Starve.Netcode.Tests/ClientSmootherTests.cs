using Starve.Netcode;

namespace Starve.Netcode.Tests;

/// <summary>
/// 组件机制的验收。全部用假模型（一维匀速 + 可选墙），不依赖任何游戏代码。
///
/// 时间约定：<b>单程延迟必须是常数</b>，否则时钟会判定"偏差过大"而硬同步
/// （这本身就是被测行为之一）。这里取 LatencyMs = 100（2 tick）：
/// 服务端 tick T 的状态在 <c>T*50 + LatencyMs</c> 到达客户端。
/// 于是"重放多少 tick" = 这份快照被处理时已过去了多少墙钟。
/// </summary>
public sealed class ClientSmootherTests
{
    private const long Tick0 = 1000;
    private const long LatencyMs = 100;

    /// <summary>第一份快照（tick 1000）到达的墙钟。</summary>
    private static long Now0 => Tick0 * 50 + LatencyMs;

    /// <summary>tick T 的状态到达客户端的墙钟。</summary>
    private static long WallOf(long tick) => tick * 50 + LatencyMs;

    private static FakeAction A(int dx) => new() { Dx = dx };

    /// <summary>tick T 的状态"迟到" extraMs 之后才被处理。</summary>
    private static long Late(long tick, long extraMs) => WallOf(tick) + extraMs;

    private static NetSnapshot<FakeState> Snap(long tick, float x, ulong seq = 1, ulong epoch = 7) =>
        new(tick, seq, epoch, new FakeState { X = x }, false);

    private static ClientSmoother<FakeState, FakeAction> New(NetcodeConfig? config = null) =>
        new(new FakeModel { Speed = 10f }, config);

    [Fact]
    public void BootstrapThenAdvanceIsDeterministic()
    {
        var smoother = New();
        var report = smoother.OnSnapshot(Snap(Tick0, 0f), Now0);

        Assert.Equal(CorrectionKind.Bootstrap, report.Kind);
        Assert.True(smoother.HasState);

        smoother.SetIntent(1, 7, A(1), Now0);
        smoother.AdvanceTo(Tick0 + 3);

        Assert.Equal(1.5f, smoother.State.X, 4); // 3 tick × 0.5 格
        Assert.Equal(Tick0 + 3, smoother.StateTick);
    }

    [Fact]
    public void AdvanceDoesNotDoubleConsumeTime()
    {
        var smoother = New();
        smoother.OnSnapshot(Snap(Tick0, 0f), Now0);
        smoother.SetIntent(1, 7, A(1), Now0);

        smoother.AdvanceTo(Tick0 + 3);
        var version = smoother.StateVersion;

        smoother.AdvanceTo(Tick0 + 3); // 同一目标再来一次：必须是 no-op
        Assert.Equal(1.5f, smoother.State.X, 4);
        Assert.Equal(version, smoother.StateVersion);

        smoother.AdvanceTo(Tick0 + 1); // 往回也不动
        Assert.Equal(1.5f, smoother.State.X, 4);
    }

    [Fact]
    public void LateSnapshotDoesNotRewindTheChain()
    {
        var smoother = New();
        smoother.OnSnapshot(Snap(Tick0, 0f), Now0);
        smoother.SetIntent(1, 7, A(1), Now0);
        smoother.AdvanceTo(Tick0 + 6);
        var version = smoother.StateVersion;
        var tick = smoother.StateTick;

        var report = smoother.OnSnapshot(Snap(Tick0 - 1, 5f, seq: 2), WallOf(Tick0 + 4));

        Assert.Equal(CorrectionKind.Stale, report.Kind);
        Assert.Equal(tick, smoother.StateTick);
        Assert.Equal(version, smoother.StateVersion); // 完全没动
        Assert.Equal(3.0f, smoother.State.X, 4);
    }

    [Fact]
    public void CorrectionRewritesTheChainWithoutRewindingTime()
    {
        // 链头 tick = "客户端已经预测到哪一刻"，它随墙钟**单调前进**：校正改的是
        // "那一刻的状态"，不是把时间倒回去。（早先版本会把终点截回"快照 + 上限"，
        // 已经算好的预测被白扔，误差还会在两个不同时刻之间比较 → 假失配。）
        // 所以"链有没有变"只能看 version，不能看 tick。
        var smoother = New();
        smoother.OnSnapshot(Snap(Tick0, 0f), Now0);
        smoother.SetIntent(1, 7, A(1), Now0);
        smoother.AdvanceTo(Tick0 + 6);
        var version = smoother.StateVersion;
        var x = smoother.State.X;

        // 权威只到 1004，而且比本地链低 0.2 格
        var report = smoother.OnSnapshot(Snap(Tick0 + 4, 1.8f, seq: 2), WallOf(Tick0 + 4));

        Assert.Equal(Tick0 + 6, smoother.StateTick);   // ★ 时间不倒流
        Assert.True(smoother.StateVersion > version);  // 但链的内容被重写了
        Assert.True(smoother.State.X < x);             // 校正后的那一刻确实更靠后
        Assert.Equal(CorrectionKind.Blended, report.Kind);
    }

    [Fact]
    public void ReplayReproducesTheDeterministicChain()
    {
        // 关掉时钟校正，让"重放多少 tick"完全由墙钟差决定（把时钟行为隔离出去）。
        var smoother = New(new NetcodeConfig { ClockMaxStepMs = 0 });
        smoother.OnSnapshot(Snap(Tick0, 0f), Now0);

        smoother.SetIntent(1, 7, A(1), Now0);      // 生效 tick = 1000
        smoother.AdvanceTo(Tick0 + 2);              // 1001,1002 → X = 1.0
        smoother.SetIntent(2, 7, A(-1), WallOf(Tick0 + 3));     // 生效 tick = 1003
        smoother.AdvanceTo(Tick0 + 4);              // 1003,1004 → X = 0.0

        // 权威在 1002 说 X = 1.0；now tick = 1010 → 重放 1002..1009 共 8 步。
        // ⚠️ 重放起点是**快照那一 tick**（1002），而新意图要到 1003 才生效：
        //    第 1 步仍属于旧意图（seq1，Dx=+1），剩下 7 步才是 Dx=-1。
        //    ⇒ 1.0 + 0.5 − 7×0.5 = −2.0（早先的测试期望 −3.0 是漏算了这一步）。
        var report = smoother.OnSnapshot(Snap(Tick0 + 2, 1.0f, seq: 2), Late(Tick0 + 2, 8 * 50));

        Assert.Equal(8, report.ReplayedTicks);
        Assert.Equal(-2.0f, smoother.State.X, 3); // 1.0 + 0.5 − 7×0.5
        Assert.Equal(Tick0 + 10, smoother.StateTick);
    }

    [Fact]
    public void StalePredictionIsRejectedAndRetriedOnce()
    {
        var model = new FakeModel { Speed = 10f, StaleReturns = 1 };
        var smoother = new ClientSmoother<FakeState, FakeAction>(model);
        smoother.OnSnapshot(Snap(Tick0, 0f), Now0);
        smoother.SetIntent(1, 7, A(1), Now0);

        smoother.AdvanceTo(Tick0 + 1);

        Assert.Equal(0.5f, smoother.State.X, 4); // 只应用一次，没有重复位移
        Assert.Equal(2, model.StepCalls);         // 被调用两次 = 重算过一次
        Assert.Equal(0, smoother.VersionMismatchCount);
    }

    [Fact]
    public void PersistentlyStalePredictionIsRecordedNotSwallowed()
    {
        var model = new FakeModel { Speed = 10f, StaleReturns = 99 };
        var smoother = new ClientSmoother<FakeState, FakeAction>(model);
        smoother.OnSnapshot(Snap(Tick0, 0f), Now0);
        smoother.SetIntent(1, 7, A(1), Now0);

        smoother.AdvanceTo(Tick0 + 1);

        Assert.Equal(0.5f, smoother.State.X, 4);      // 仍然采纳，避免卡死
        Assert.Equal(1, smoother.VersionMismatchCount); // 但必须记账（调用方应报警）
    }

    [Fact]
    public void CorrectionTiersFollowTheErrorSize()
    {
        var smoother = New();
        smoother.OnSnapshot(Snap(Tick0, 0f), Now0);
        smoother.SetIntent(1, 7, A(1), Now0);
        smoother.AdvanceTo(Tick0 + 5); // X = 2.5

        Assert.Equal(CorrectionKind.Deadzone,
            smoother.OnSnapshot(Snap(Tick0 + 5, 2.5f, seq: 2), WallOf(Tick0 + 5)).Kind);
        Assert.Equal(CorrectionKind.Blended, // 差 0.5：可掩饰 → 平滑
            smoother.OnSnapshot(Snap(Tick0 + 6, 2.0f, seq: 3), WallOf(Tick0 + 6)).Kind);
        Assert.Equal(CorrectionKind.Snapped, // 差很大：撞墙/传送 → 直接贴
            smoother.OnSnapshot(Snap(Tick0 + 7, 100f, seq: 4), WallOf(Tick0 + 7)).Kind);
    }

    [Fact]
    public void OnCorrectedFiresOnlyForRealCorrections()
    {
        var smoother = New();
        smoother.OnSnapshot(Snap(Tick0, 0f), Now0);
        smoother.SetIntent(1, 7, A(1), Now0);
        smoother.AdvanceTo(Tick0 + 5);

        var calls = 0;
        CorrectionKind last = CorrectionKind.None;
        smoother.OnCorrected = report =>
        {
            calls++;
            last = report.Kind;
            // 回调里读到的就是"校正后的最新状态"——外部不需要（也不应该）重放中间帧。
            Assert.True(smoother.StateVersion == report.StateVersion);
        };

        smoother.OnSnapshot(Snap(Tick0 + 5, 2.5f, seq: 2), WallOf(Tick0 + 5)); // Deadzone：不通知
        Assert.Equal(0, calls);

        smoother.OnSnapshot(Snap(Tick0 + 6, 2.0f, seq: 3), WallOf(Tick0 + 6)); // Blended
        Assert.Equal(1, calls);
        Assert.Equal(CorrectionKind.Blended, last);

        smoother.OnSnapshot(Snap(Tick0 + 7, 100f, seq: 4), WallOf(Tick0 + 7)); // Snapped
        Assert.Equal(2, calls);
        Assert.Equal(CorrectionKind.Snapped, last);
    }

    [Fact]
    public void ReplayIsClampedToMaxReplayTicks()
    {
        var smoother = New(new NetcodeConfig { MaxReplayTicks = 3, ClockMaxStepMs = 0 });
        smoother.OnSnapshot(Snap(Tick0, 0f), Now0);
        smoother.SetIntent(1, 7, A(1), Now0);

        // 快照只到 tick 1002，但它晚了 18 tick 才被处理 → 想重放 18 tick，被截到 3
        var report = smoother.OnSnapshot(Snap(Tick0 + 2, 1.0f, seq: 2), Late(Tick0 + 2, 18 * 50));

        Assert.True(report.ReplayClamped);
        Assert.Equal(3, report.ReplayedTicks);
        Assert.Equal(Tick0 + 5, smoother.StateTick);
    }

    [Fact]
    public void SameTickSpanSpeedComparisonIsLatencyFree()
    {
        // 这是"预测准不准"的正确判据：同一 tick 跨度内，本地纯预测走了多少 vs 服务端走了多少。
        var smoother = New();
        smoother.OnSnapshot(Snap(Tick0, 0f), Now0);
        smoother.SetIntent(1, 7, A(1), Now0);

        smoother.AdvanceTo(Tick0 + 1);                       // 本地纯预测走 0.5 格
        var report = smoother.OnSnapshot(Snap(Tick0 + 1, 0.5f, seq: 2), WallOf(Tick0 + 1));

        Assert.Equal(10f, report.AuthoritativeStepSpeed, 2); // 0.5 格 / 1 tick = 10 格/秒
        Assert.Equal(10f, report.PredictedStepSpeed, 2);
    }

    [Fact]
    public void PredictionSlowerThanServerShowsUpInTheSpeedGap()
    {
        var model = new FakeModel { Speed = 5f }; // 本地只有服务端一半速度
        var smoother = new ClientSmoother<FakeState, FakeAction>(model);
        smoother.OnSnapshot(Snap(Tick0, 0f), Now0);
        smoother.SetIntent(1, 7, A(1), Now0);

        smoother.AdvanceTo(Tick0 + 1);                        // 本地走 0.25
        var report = smoother.OnSnapshot(Snap(Tick0 + 1, 0.5f, seq: 2), WallOf(Tick0 + 1)); // 服务端走 0.5

        Assert.Equal(10f, report.AuthoritativeStepSpeed, 2);
        Assert.Equal(5f, report.PredictedStepSpeed, 2);
    }

    [Fact]
    public void EpochChangeRebuildsTheChain()
    {
        var smoother = New();
        smoother.OnSnapshot(Snap(Tick0, 0f), Now0);
        smoother.SetIntent(1, 7, A(1), Now0);
        smoother.AdvanceTo(Tick0 + 3);

        var report = smoother.OnSnapshot(Snap(Tick0, 9f, seq: 1, epoch: 8), Now0);

        Assert.Equal(CorrectionKind.Bootstrap, report.Kind); // 新会话：重新建立
        Assert.Equal(9f, smoother.State.X, 4);
        Assert.Equal(0, smoother.Inputs.Count);
    }

    [Fact]
    public void StarvedIsReportedAfterStallWindow()
    {
        var smoother = New(new NetcodeConfig { StallMs = 1000 }); // 20 tick
        smoother.OnSnapshot(Snap(Tick0, 0f), Now0);

        Assert.False(smoother.Starved(Tick0 + 19));
        Assert.True(smoother.Starved(Tick0 + 21));
    }

    [Fact]
    public void RemoteEntitiesAreKeyedAndShareTheClock()
    {
        var smoother = New();
        var a = smoother.Remote(42);
        var b = smoother.Remote(42);
        Assert.Same(a, b);

        a.Push(Tick0, new FakeState { X = 1f });
        Assert.True(smoother.RemoveRemote(42));
        Assert.Empty(smoother.Remotes);
    }
}
