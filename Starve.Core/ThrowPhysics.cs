using System;
using System.Numerics;

namespace Starve.Core;

/// <summary>
/// 投掷物理：与服务端 <c>internal/game/components/throwable.go</c> **同一套公式**。
///
/// 分工（与用户确认的设计）：
///   - 客户端用同一公式算"能扔多远 + 抛物线长什么样"，**本地即时反馈**，
///     不需要往返服务端；
///   - 服务端仍然权威校验（距离/落点/起点一致性），客户端算错只会被拒，
///     不会造成不一致的状态。
///
/// 为什么用整数除法算距离：结果参与**校验**，必须确定性与两端一致。
/// 浮点在 Go 与 C# 之间、以及不同平台之间都可能有微小差异；
/// 整数除法没有这个问题。
/// </summary>
public static class ThrowPhysics
{
    /// <summary>基础投掷距离（格）：力量 == 质量 时的距离。与服务端一致。</summary>
    public const int BaseThrowDistance = 8;

    /// <summary>默认重力（格/tick²）。与服务端一致。</summary>
    public const double DefaultGravity = 0.02;

    /// <summary>每秒 tick 数（服务端 20Hz）。</summary>
    public const double TicksPerSecond = 20.0;

    /// <summary>
    /// 最大投掷距离（格）= 基础距离 × 力量 / 质量。
    ///
    /// 返回 0 表示不能投掷（力量或质量非正）。
    /// 与服务端 <c>MaxThrowDistance</c> 完全一致（整数除法）。
    /// </summary>
    public static int MaxThrowDistance(int strength, int mass)
    {
        if (strength <= 0 || mass <= 0) return 0;
        return BaseThrowDistance * strength / mass;
    }

    /// <summary>
    /// 由水平距离与重力反推飞行时长（tick）。
    ///
    /// 与服务端 <c>FlightTicks</c> 一致：t = sqrt(2d/g)，四舍五入，至少 1。
    /// </summary>
    public static int FlightTicks(double distance, double gravity = DefaultGravity)
    {
        if (gravity <= 0) gravity = DefaultGravity;
        if (distance <= 0) return 1;
        var t = Math.Sqrt(2 * distance / gravity);
        var ticks = (int)Math.Round(t);
        return ticks < 1 ? 1 : ticks;
    }

    /// <summary>
    /// 抛物线峰值高度（格）。与服务端 <c>PeakHeight</c> 一致：h = g·t²/8。
    /// </summary>
    public static double PeakHeight(int flightTicks, double gravity = DefaultGravity)
    {
        if (gravity <= 0) gravity = DefaultGravity;
        if (flightTicks <= 0) return 0;
        var t = (double)flightTicks;
        return gravity * t * t / 8;
    }

    /// <summary>
    /// 计算从 from 到 to 的抛物线参数。
    ///
    /// 水平距离用**欧氏**距离（抛物线是圆对称的物理轨迹），
    /// 与服务端一致；注意这与 AOI 感知/仇恨传播的方形（切比雪夫）口径不同。
    /// </summary>
    public static ThrowTrajectory Solve(Vector2 from, Vector2 to, double gravity = DefaultGravity)
    {
        if (gravity <= 0) gravity = DefaultGravity;
        var d = Vector2.Distance(from, to);
        var ticks = FlightTicks(d, gravity);
        return new ThrowTrajectory
        {
            From = from,
            To = to,
            FlightTicks = ticks,
            PeakHeight = PeakHeight(ticks, gravity),
            Gravity = gravity,
        };
    }

    /// <summary>
    /// 取飞行进度 t ∈ [0,1] 时的高度偏移（格）。
    ///
    /// 对称抛物线：h(t) = 4·peak·t·(1-t)，峰值在 t=0.5。
    /// 渲染时把该高度加到屏幕 Y 上（减去，因为屏幕 Y 向下）。
    /// </summary>
    public static double HeightAt(double t, double peakHeight)
    {
        if (t <= 0 || t >= 1) return 0;
        return 4 * peakHeight * t * (1 - t);
    }
}

/// <summary>一条抛物线的参数（客户端渲染用；与服务端 ThrowArc 对应）。</summary>
public readonly struct ThrowTrajectory
{
    public Vector2 From { get; init; }
    public Vector2 To { get; init; }
    public int FlightTicks { get; init; }
    public double PeakHeight { get; init; }
    public double Gravity { get; init; }

    /// <summary>飞行时长（秒）。</summary>
    public double FlightSeconds => FlightTicks / ThrowPhysics.TicksPerSecond;

    /// <summary>
    /// 按已飞 tick 数取当前水平位置与高度（格）。
    /// 水平匀速 —— 最后一 tick 精确落在 To。
    /// </summary>
    public (Vector2 Pos, double Height) SampleAt(int elapsedTicks) =>
        SampleAt((double)elapsedTicks);

    /// <summary>
    /// 按已飞 tick 数（**double**）取当前水平位置与高度（格）。
    ///
    /// 为什么需要 double 版本：权威 <c>elapsed</c> 只有 20Hz，客户端在两次快照之间
    /// 按帧时间自行推进。若每帧把它截断成 int 再采样，位置就只能按 20Hz 跳 ——
    /// 正是"炸弹一格一顿"的成因。这里只是把 t 的精度放开，公式与
    /// <see cref="SampleAt(int)"/> 完全同一份（后者直接转发到本方法），
    /// 因此整数 tick 的结果逐位不变，落地仍精确落在 To、高度归零。
    /// </summary>
    public (Vector2 Pos, double Height) SampleAt(double elapsedTicks)
    {
        var t = FlightTicks <= 0 ? 1.0 : Math.Clamp(elapsedTicks / FlightTicks, 0, 1);
        var pos = Vector2.Lerp(From, To, (float)t);
        return (pos, ThrowPhysics.HeightAt(t, PeakHeight));
    }
}
