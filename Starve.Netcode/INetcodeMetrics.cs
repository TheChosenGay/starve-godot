namespace Starve.Netcode;

/// <summary>
/// 组件指标出口。**真实业务实现它**，把数据接到自己的日志/监控上。
///
/// 组件只在事件真的发生时才调用（未设置时为 null，组件不做任何额外工作）：
/// <list type="bullet">
/// <item><see cref="OnCorrection"/>：每份被处理的权威快照一次（约 20/秒）。
///   里面带着 <c>Kind</c>（Bootstrap/Stale/Deadzone/Blended/Snapped）、误差、
///   重放了多少 tick、以及"同一 tick 跨度"的速度对比 —— 足够算接受率/重放率/回退率。</item>
/// <item><see cref="OnClockResync"/>：时间基准被重建时（换会话 / tick 倒退 / 偏差离谱），很少发生。</item>
/// <item><see cref="OnRemoteSample"/>：每帧每个远端实体一次（**热路径**，可能几百/帧）。
///   不需要就留空（默认实现就是空的）。</item>
/// </list>
/// </summary>
public interface INetcodeMetrics
{
    /// <summary>一份权威快照的和解结果。</summary>
    void OnCorrection(in CorrectionReport report);

    /// <summary>每帧每个远端实体的采样结果（热路径；不需要就别覆写）。</summary>
    void OnRemoteSample(ulong entityId, in RemoteSampleReport report)
    {
    }

    /// <summary>时间基准被重建。</summary>
    void OnClockResync(long serverTick, long nowMs, double offsetMs)
    {
    }
}
