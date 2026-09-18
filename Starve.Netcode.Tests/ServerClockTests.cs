using Starve.Netcode;

namespace Starve.Netcode.Tests;

public sealed class ServerClockTests
{
    [Fact]
    public void UnsynchronizedReturnsZero()
    {
        var clock = new ServerClock();
        Assert.False(clock.HasSync);
        Assert.Equal(0, clock.TickAt(123_456), 6);
    }

    [Fact]
    public void FirstSnapshotEstablishesExactMapping()
    {
        var clock = new ServerClock();
        clock.OnSnapshot(1000, 50_000);
        Assert.True(clock.HasSync);
        Assert.Equal(1000, clock.TickAt(50_000), 6);
        Assert.Equal(1002, clock.TickAt(50_100), 6); // 100ms = 2 tick
    }

    [Fact]
    public void CorrectionIsProportionalAndRateLimited()
    {
        var clock = new ServerClock(new NetcodeConfig { ClockMaxStepMs = 2, ClockCorrectionRatio = 0.1 });
        clock.OnSnapshot(1000, 50_000); // offset = 0
        clock.OnSnapshot(1000, 50_100); // 观测偏移 100ms

        Assert.False(clock.HardResynced);
        Assert.Equal(2.0, clock.OffsetMs, 6); // 比例 10ms 被上限 2ms 截住
        Assert.Equal((50_100 - 2.0) / 50.0, clock.TickAt(50_100), 6);
    }

    [Fact]
    public void SingleLateSnapshotDoesNotResetTheTimeBase()
    {
        // ⚠️ 这条是"把一次延迟放大成一次冻结"那个坑的回归测试：
        // 一个迟到 400ms 的包绝不能让时间基准整体挪走（否则重放长度会变成 0）。
        var clock = new ServerClock(new NetcodeConfig
        {
            ClockMaxStepMs = 10,
            ClockCorrectionRatio = 0.1,
            ClockResyncMs = 5000,
        });
        clock.OnSnapshot(1000, 50_100); // offset = 100（单程延迟）

        clock.OnSnapshot(1002, 50_600); // 迟到 400ms

        Assert.False(clock.HardResynced);
        Assert.Equal(110.0, clock.OffsetMs, 6);                  // 只挪了 10ms
        Assert.Equal((50_600 - 110.0) / 50.0, clock.TickAt(50_600), 6); // ≈ 1009.8，而不是 1002
    }

    [Fact]
    public void AbsurdDriftHardResyncs()
    {
        var clock = new ServerClock(new NetcodeConfig { ClockResyncMs = 5000 });
        clock.OnSnapshot(1000, 50_000);
        clock.OnSnapshot(1000, 56_000); // 偏差 6000ms
        Assert.True(clock.HardResynced);
        Assert.Equal(6000.0, clock.OffsetMs, 6);
    }

    [Fact]
    public void EpochChangeHardResyncs()
    {
        var clock = new ServerClock();
        clock.OnSnapshot(1000, 50_000, epoch: 7);
        clock.OnSnapshot(10, 50_500, epoch: 8); // 换会话：tick 归零
        Assert.True(clock.HardResynced);
        Assert.Equal(10.0, clock.TickAt(50_500), 6);
    }

    [Fact]
    public void BackwardsTickHardResyncs()
    {
        var clock = new ServerClock();
        clock.OnSnapshot(1000, 50_000);
        clock.OnSnapshot(900, 50_050); // 服务端重启/坏数据
        Assert.True(clock.HardResynced);
        Assert.Equal(900.0, clock.TickAt(50_050), 6);
    }

    [Fact]
    public void LeadTicksMeasuresHowFarAheadWeAre()
    {
        var clock = new ServerClock();
        clock.OnSnapshot(1000, 50_000);
        Assert.Equal(4.0, clock.LeadTicks(50_200, 1000), 6);
    }
}
