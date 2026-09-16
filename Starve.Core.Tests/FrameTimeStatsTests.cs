using Starve.Core;

namespace Starve.Core.Tests;

public sealed class FrameTimeStatsTests
{
    [Fact]
    public void EmptyReportIsSafe()
    {
        var stats = new FrameTimeStats();
        var r = stats.Report();
        Assert.Equal(0, r.Samples);
        Assert.Equal(1f, r.JitterRatio); // 除零保护
        Assert.Equal(0f, r.SpikeRatio);
    }

    [Fact]
    public void IgnoresNonPositiveAndInvalidSamples()
    {
        var stats = new FrameTimeStats();
        stats.Add(0);
        stats.Add(-5);
        stats.Add(double.NaN);
        stats.Add(double.PositiveInfinity);
        Assert.Equal(0, stats.Count);
    }

    [Fact]
    public void StableFrameTimesReportNoSpikes()
    {
        var stats = new FrameTimeStats();
        for (var i = 0; i < 600; i++) stats.Add(10.0);
        var r = stats.Report();
        Assert.Equal(600, r.Samples);
        Assert.Equal(10f, r.MedianMs, 3);
        Assert.Equal(0, r.SpikeCount);
        Assert.Equal(1f, r.JitterRatio, 3);
    }

    // 这正是实测客户端出现过的情况：中位 12.3ms、尖峰 71.4ms、
    // 平均帧率看着不错，但手感是卡的。
    [Fact]
    public void DetectsSpikesThatAverageHides()
    {
        var stats = new FrameTimeStats();
        for (var i = 0; i < 120; i++) stats.Add(12.0);
        stats.Add(71.4); // 一个尖峰
        var r = stats.Report();
        Assert.Equal(121, r.Samples);
        Assert.Equal(12f, r.MedianMs, 3);
        Assert.Equal(71.4f, r.WorstMs, 3);
        Assert.Equal(1, r.SpikeCount);
        // 抖动比基于**最坏帧**（P99 取不到单帧尖峰，见 JitterRatio 注释）
        Assert.True(r.JitterRatio > 5f, $"最坏/中位 应显著大于 1，实际 {r.JitterRatio}");
        Assert.Equal(1f / 121f, r.SpikeRatio, 4);
    }

    [Fact]
    public void RingBufferKeepsOnlyRecentFrames()
    {
        var stats = new FrameTimeStats(capacity: 10);
        for (var i = 0; i < 100; i++) stats.Add(i < 50 ? 5.0 : 20.0);
        var r = stats.Report();
        Assert.Equal(10, r.Samples);
        Assert.Equal(100, stats.TotalFrames);
        // 窗口里只剩后 10 帧（都是 20ms）
        Assert.Equal(20f, r.MedianMs, 3);
        Assert.Equal(20f, r.WorstMs, 3);
    }

    [Fact]
    public void PercentilesAreOrdered()
    {
        var stats = new FrameTimeStats();
        for (var i = 1; i <= 100; i++) stats.Add(i);
        var r = stats.Report();
        Assert.True(r.MedianMs <= r.P95Ms);
        Assert.True(r.P95Ms <= r.P99Ms);
        Assert.True(r.P99Ms <= r.WorstMs);
    }

    [Fact]
    public void ClearResetsWindowButKeepsTotal()
    {
        var stats = new FrameTimeStats();
        stats.Add(10);
        stats.Clear();
        Assert.Equal(0, stats.Count);
        Assert.Equal(0, stats.Report().Samples);
        Assert.Equal(1, stats.TotalFrames);
    }
}
