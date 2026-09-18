using Starve.Netcode;

namespace Starve.Netcode.Tests;

public sealed class InputHistoryTests
{
    private static FakeAction A(int dx) => new() { Dx = dx };

    [Fact]
    public void ActionAtTickReturnsTheEffectiveRecord()
    {
        var history = new InputHistory<FakeAction>();
        history.Record(1, epoch: 7, baseTick: 100.0, effectiveTick: 100.0, A(1));
        history.Record(2, epoch: 7, baseTick: 103.0, effectiveTick: 103.0, A(0));

        Assert.True(history.TryActionAtTick(100.5, out var at100));
        Assert.Equal(1, at100.Dx);
        Assert.True(history.TryActionAtTick(102.9, out var at102));
        Assert.Equal(1, at102.Dx);
        Assert.True(history.TryActionAtTick(103.5, out var at103));
        Assert.Equal(0, at103.Dx);
        Assert.True(history.TryActionAtTick(999.0, out var later));
        Assert.Equal(0, later.Dx);
    }

    [Fact]
    public void BeforeAnyRecordFallsBackToLatestIntent()
    {
        var history = new InputHistory<FakeAction>();
        history.Record(1, 7, 100.0, 100.0, A(1));
        Assert.True(history.TryActionAtTick(50.0, out var action));
        Assert.Equal(1, action.Dx);
    }

    [Fact]
    public void EmptyHistoryHasNoAction()
    {
        var history = new InputHistory<FakeAction>();
        Assert.False(history.TryActionAtTick(10, out _));
    }

    [Fact]
    public void AcknowledgeTrimsHistoryButKeepsCurrentIntent()
    {
        var history = new InputHistory<FakeAction>();
        history.Record(1, 7, 100.0, 100.0, A(1));
        history.Record(2, 7, 101.0, 101.0, A(1));
        history.Acknowledge(2);

        Assert.Equal(0, history.Count);       // 已确认的都被丢掉
        Assert.True(history.HasLatest);        // 但"当前意图"要留着给后续 tick 兜底
        Assert.True(history.TryActionAtTick(500, out var action));
        Assert.Equal(1, action.Dx);
    }

    [Fact]
    public void EpochChangeClears()
    {
        var history = new InputHistory<FakeAction>();
        history.Record(1, epoch: 7, baseTick: 100.0, effectiveTick: 100.0, A(1));
        history.Record(1, epoch: 8, baseTick: 100.0, effectiveTick: 100.0, A(-1));

        Assert.Equal(8UL, history.Epoch);
        Assert.Equal(1, history.Count);
        Assert.True(history.TryActionAtTick(100, out var action));
        Assert.Equal(-1, action.Dx);
    }

    [Fact]
    public void OutOfOrderOrDuplicateSeqIgnored()
    {
        var history = new InputHistory<FakeAction>();
        history.Record(5, 7, 100.0, 100.0, A(1));
        history.Record(3, 7, 101.0, 101.0, A(-1)); // 旧 seq：忽略
        history.Record(5, 7, 102.0, 102.0, A(-1)); // 重复 seq：忽略

        Assert.Equal(1, history.Count);
        Assert.Equal(5UL, history.LatestSeq);
        Assert.Equal(1, history.Latest.Dx);
    }

    [Fact]
    public void CapacityDropsOldest()
    {
        var history = new InputHistory<FakeAction>(capacity: 3);
        for (var i = 1; i <= 5; i++) history.Record((ulong)i, 7, 100.0 + i, 100.0 + i, A(i));

        Assert.Equal(3, history.Count);
        // 最早那两条（seq1/seq2）已被挤掉：tick 102.5 落不到任何记录上 → 回落到 Latest
        Assert.True(history.TryActionAtTick(102.5, out var fallback));
        Assert.Equal(5, fallback.Dx);
        // 保留下来的最早一条是 seq3（生效 tick 103）
        Assert.True(history.TryActionAtTick(103.5, out var oldest));
        Assert.Equal(3, oldest.Dx);
    }
}
