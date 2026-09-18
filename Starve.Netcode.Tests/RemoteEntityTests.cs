using Starve.Netcode;

namespace Starve.Netcode.Tests;

/// <summary>别人（远端实体）的表现层：插值 + 有界外推。不预测，只用真实样本。</summary>
public sealed class RemoteEntityTests
{
    private static FakeState S(float x) => new() { X = x };

    private static RemoteEntity<FakeState> New(NetcodeConfig config) =>
        new(new FakeModel(), config);

    [Fact]
    public void EmptyWithoutSamples()
    {
        var remote = New(new NetcodeConfig());
        Assert.False(remote.HasData);
        remote.Sample(100, out var report);
        Assert.Equal(RemoteSampleKind.Empty, report.Kind);
    }

    [Fact]
    public void InterpolatesBetweenRealSamples()
    {
        var remote = New(new NetcodeConfig { InterpolationDelayTicks = 1 });
        remote.Push(100, S(10f));
        remote.Push(101, S(15f));

        // playout = 101.5 - 1 = 100.5 → 10..15 之间取一半
        var state = remote.Sample(101.5, out var report);

        Assert.Equal(12.5f, state.X, 4);
        Assert.Equal(RemoteSampleKind.Interpolated, report.Kind);
        Assert.Equal(100, report.FromTick);
        Assert.Equal(101, report.ToTick);
        Assert.Equal(0.5f, report.Alpha, 4);
    }

    [Fact]
    public void PlayoutIsRenderedInThePast()
    {
        var remote = New(new NetcodeConfig { InterpolationDelayTicks = 2 });
        remote.Push(100, S(10f));
        remote.Push(101, S(20f));
        remote.Push(102, S(30f));

        // now = 102 → playout = 100 → 完全是第一个样本（而不是最新样本）
        Assert.Equal(10f, remote.Sample(102, out var report).X, 4);
        Assert.Equal(100.0, report.PlayoutTick, 4);
    }

    [Fact]
    public void ExtrapolatesAlongTheLastRealSegmentWithinCap()
    {
        var remote = New(new NetcodeConfig
        {
            InterpolationDelayTicks = 1,
            MaxExtrapolationTicks = 1,
        });
        remote.Push(100, S(10f));
        remote.Push(101, S(15f)); // 每 tick +5

        // playout = 102.5 - 1 = 101.5 → beyond 0.5 → 沿真实段继续走 15 + 0.5×5
        var state = remote.Sample(102.5, out var report);
        Assert.Equal(17.5f, state.X, 4);
        Assert.Equal(RemoteSampleKind.Extrapolated, report.Kind);
    }

    [Fact]
    public void FreezesAtTheCapAndNeverGoesBackward()
    {
        var remote = New(new NetcodeConfig
        {
            InterpolationDelayTicks = 1,
            MaxExtrapolationTicks = 1,
        });
        remote.Push(100, S(10f));
        remote.Push(101, S(15f));

        // beyond = 1.5 > cap → 冻结在最新样本
        Assert.Equal(15f, remote.Sample(103.5, out var frozen).X, 4);
        Assert.Equal(RemoteSampleKind.Frozen, frozen.Kind);

        // 再往后也是同一个位置（绝不倒退、绝不用"先冲出去再回来"的写法）
        Assert.Equal(15f, remote.Sample(140, out _).X, 4);
    }

    [Fact]
    public void FrozenWhenExtrapolationDisabled()
    {
        var remote = New(new NetcodeConfig
        {
            InterpolationDelayTicks = 0,
            MaxExtrapolationTicks = 5,
            Extrapolate = false,
        });
        remote.Push(100, S(10f));
        remote.Push(101, S(15f));

        Assert.Equal(15f, remote.Sample(101.5, out var report).X, 4);
        Assert.Equal(RemoteSampleKind.Frozen, report.Kind);
    }

    [Fact]
    public void StoppedEntityDoesNotDriftBecauseLastSegmentHasNoSpeed()
    {
        // 贴墙/停下时最后一段本来就没位移 → 外推自然不动（不需要服务端速度可信）。
        var remote = New(new NetcodeConfig
        {
            InterpolationDelayTicks = 0,
            MaxExtrapolationTicks = 2,
        });
        remote.Push(100, S(7f));
        remote.Push(101, S(7f));
        remote.Push(102, S(7f));

        Assert.Equal(7f, remote.Sample(103.5, out var report).X, 4);
        Assert.Equal(RemoteSampleKind.Extrapolated, report.Kind); // 走了外推分支，但位移为 0
    }

    [Fact]
    public void StarvedAfterStallWindow()
    {
        var remote = New(new NetcodeConfig { StallMs = 200 }); // 4 tick
        remote.Push(100, S(10f));

        remote.Sample(104, out var fresh);
        Assert.False(fresh.Starved);

        remote.Sample(106, out var starved);
        Assert.True(starved.Starved);
    }

    [Fact]
    public void StalePushDroppedAndSameTickWins()
    {
        var remote = New(new NetcodeConfig());
        remote.Push(100, S(10f));
        remote.Push(101, S(15f));

        Assert.False(remote.Push(99, S(0f)));  // 更旧：丢弃
        Assert.Equal(2, remote.Buffered);

        Assert.True(remote.Push(101, S(42f))); // 同 tick：后者胜
        Assert.Equal(2, remote.Buffered);
        Assert.Equal(42f, remote.Sample(101 + 5, out _).X, 4);
    }

    [Fact]
    public void BeforeFirstSampleUsesFirstSample()
    {
        var remote = New(new NetcodeConfig { InterpolationDelayTicks = 3 });
        remote.Push(100, S(10f));
        remote.Push(101, S(15f));

        // playout 还在第一个样本之前：没有更早的数据可用
        Assert.Equal(10f, remote.Sample(100, out var report).X, 4);
        Assert.Equal(RemoteSampleKind.Interpolated, report.Kind);
    }
}
