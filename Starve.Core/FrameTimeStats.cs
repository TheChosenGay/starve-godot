using System;
using System.Collections.Generic;

namespace Starve.Core;

/// <summary>
/// 帧时间直方图：记录最近一段时间的**每帧**间隔，输出分位数与尖峰统计。
///
/// 为什么需要它：<see cref="PerfSnapshot"/> 是每秒一条的**平均值**，
/// 平均 FPS 正常不代表不卡——帧时间忽长忽短（frame pacing 问题）在视觉上
/// 就是"一顿一顿的"，但会被平均值掩盖。实测客户端出现过
/// "中位 12.3ms、尖峰 71.4ms、标准差 16.3ms" 的情况，平均帧率看着很好，
/// 手感却是卡的。这个类专门用来抓那种尖峰。
///
/// 纯逻辑（不依赖 Godot），时间由外部注入，便于单测。
/// </summary>
public sealed class FrameTimeStats
{
    /// <summary>保留的帧数上限（环形缓冲）。1200 帧 @60fps ≈ 20 秒窗口。</summary>
    public const int DefaultCapacity = 1200;

    private readonly float[] _ring;
    private readonly float[] _scratch;
    private int _count;
    private int _next;

    /// <summary>超过该倍数的中位数即记为"尖峰"（缺省 2 倍）。</summary>
    public float SpikeRatio { get; }

    public FrameTimeStats(int capacity = DefaultCapacity, float spikeRatio = 2f)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _ring = new float[capacity];
        _scratch = new float[capacity];
        SpikeRatio = spikeRatio > 1f ? spikeRatio : 2f;
    }

    /// <summary>已记录的帧数（达到容量后等于容量）。</summary>
    public int Count => _count;

    /// <summary>累计记录过的总帧数（含被覆盖的）。</summary>
    public long TotalFrames { get; private set; }

    /// <summary>记录一帧的耗时（毫秒）。0 或负值忽略（暂停/首帧）。</summary>
    public void Add(double frameMs)
    {
        if (frameMs <= 0 || double.IsNaN(frameMs) || double.IsInfinity(frameMs)) return;
        _ring[_next] = (float)frameMs;
        _next = (_next + 1) % _ring.Length;
        if (_count < _ring.Length) _count++;
        TotalFrames++;
    }

    public void Clear()
    {
        _count = 0;
        _next = 0;
    }

    /// <summary>取已排序 span 的分位数（q ∈ [0,1]）。</summary>
    private static float Percentile(ReadOnlySpan<float> sorted, float q)
    {
        if (sorted.Length == 0) return 0f;
        var idx = (int)MathF.Round((sorted.Length - 1) * q);
        return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
    }

    /// <summary>
    /// 取当前窗口的统计快照。无样本时返回 <see cref="FrameTimeReport.Empty"/>。
    /// </summary>
    public FrameTimeReport Report()
    {
        if (_count == 0) return FrameTimeReport.Empty;

        Array.Copy(_ring, _scratch, _count);
        var span = _scratch.AsSpan(0, _count);
        span.Sort();

        var median = Percentile(span, 0.5f);
        var spikeThreshold = median * SpikeRatio;
        var spikes = 0;
        var worst = 0f;
        var sum = 0f;
        for (var i = 0; i < _count; i++)
        {
            var v = span[i];
            sum += v;
            if (v > worst) worst = v;
            if (v > spikeThreshold) spikes++;
        }

        return new FrameTimeReport(
            Samples: _count,
            MedianMs: median,
            P95Ms: Percentile(span, 0.95f),
            P99Ms: Percentile(span, 0.99f),
            WorstMs: worst,
            AverageMs: sum / _count,
            SpikeCount: spikes,
            SpikeThresholdMs: spikeThreshold);
    }
}

/// <summary>帧时间统计结果（毫秒）。</summary>
public readonly record struct FrameTimeReport(
    int Samples,
    float MedianMs,
    float P95Ms,
    float P99Ms,
    float WorstMs,
    float AverageMs,
    int SpikeCount,
    float SpikeThresholdMs)
{
    public static readonly FrameTimeReport Empty =
        new(0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>尖峰占比（0..1）。判断"手感卡不卡"比平均 FPS 更准。</summary>
    public float SpikeRatio => Samples <= 0 ? 0f : SpikeCount / (float)Samples;

    /// <summary>
    /// 抖动比：**最坏帧**与中位数之比。接近 1 = 帧时间稳定。
    ///
    /// 为什么用 Worst 而不是 P99：偶发尖峰（例如 121 帧里 1 帧）
    /// 在 P99 上取不到——P99 仍落在正常帧里，比值会是 1，看不出问题。
    /// 而"手感卡不卡"恰恰由最坏的那几帧决定。
    /// </summary>
    public float JitterRatio => MedianMs <= 0 ? 1f : WorstMs / MedianMs;
}
