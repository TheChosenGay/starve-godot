using Starve.Netcode;

namespace Starve.Netcode.Tests;

public sealed class SnapshotWindowTests
{
    private static FakeState S(float x) => new() { X = x };

    [Fact]
    public void InsertKeepsTickOrderAndDropsStale()
    {
        var window = new SnapshotWindow<FakeState>();
        Assert.True(window.Insert(100, S(1f), appliedSeq: 1, epoch: 7));
        Assert.True(window.Insert(101, S(2f), appliedSeq: 2, epoch: 7));
        Assert.False(window.Insert(99, S(0f), appliedSeq: 3, epoch: 7)); // 更旧：丢弃

        Assert.Equal(2, window.Count);
        Assert.Equal(101, window.LatestTick);
        Assert.True(window.TryLatest(out var latest));
        Assert.Equal(2f, latest.State.X, 4);
    }

    [Fact]
    public void SameTickLaterWins()
    {
        var window = new SnapshotWindow<FakeState>();
        window.Insert(100, S(1f), appliedSeq: 1, epoch: 7);
        window.Insert(100, S(9f), appliedSeq: 5, epoch: 7);

        Assert.Equal(1, window.Count);
        Assert.True(window.TryLatest(out var latest));
        Assert.Equal(9f, latest.State.X, 4);
        Assert.Equal(5UL, latest.AppliedSeq);
    }

    [Fact]
    public void EpochChangeClears()
    {
        var window = new SnapshotWindow<FakeState>();
        window.Insert(100, S(1f), appliedSeq: 1, epoch: 7);
        window.Insert(100, S(2f), appliedSeq: 1, epoch: 8);

        Assert.Equal(1, window.Count);
        Assert.Equal(8UL, window.Epoch);
    }

    [Fact]
    public void CapacityTrimsOldest()
    {
        var window = new SnapshotWindow<FakeState>(capacity: 3);
        for (var i = 0; i < 6; i++) window.Insert(100 + i, S(i), appliedSeq: (ulong)i, epoch: 7);

        Assert.Equal(3, window.Count);
        Assert.Equal(103, window.OldestTick);
        Assert.Equal(105, window.LatestTick);
    }

    [Fact]
    public void PreviousGivesTheSpanForLatencyFreeComparison()
    {
        var window = new SnapshotWindow<FakeState>();
        window.Insert(100, S(10f), appliedSeq: 1, epoch: 7);
        window.Insert(101, S(10.5f), appliedSeq: 2, epoch: 7);

        Assert.True(window.TryPrevious(out var previous));
        Assert.Equal(100, previous.Tick);
        Assert.Equal(10f, previous.State.X, 4);
    }
}
