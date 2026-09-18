namespace Starve.Netcode;

/// <summary>一次和解做了什么决定。</summary>
public enum CorrectionKind
{
    /// <summary>还没发生过和解。</summary>
    None,

    /// <summary>首帧/加入/重连：链由权威状态建立。</summary>
    Bootstrap,

    /// <summary>快照比窗口里最新的还旧（迟到包）：只吃它的 ack，不动链。</summary>
    Stale,

    /// <summary>误差在死区内：判定为噪声，直接对齐（不做平滑）。</summary>
    Deadzone,

    /// <summary>走了"Rebase + 重放"，残差改为**渲染层平滑过渡**（模拟状态已就位）。</summary>
    Blended,

    /// <summary>残差过大（撞墙/被顶开/传送）：不掩饰，渲染直接贴上去。</summary>
    Snapped,
}

/// <summary>
/// 一次和解的完整诊断（只读，纯数据，可直接喂日志）。
///
/// 关键：<see cref="Err"/> 是 <b>Rebase + 重放之后</b>测出来的，
/// 所以它才是"真失配"，不含网络领先量。
/// </summary>
public readonly record struct CorrectionReport(
    CorrectionKind Kind,
    long SnapshotTick,
    long StateTick,
    uint StateVersion,
    ulong AppliedSeq,
    float Err,
    int ReplayedTicks,
    bool ReplayClamped,
    bool VersionMismatch,
    /// <summary>本区间的本地预测步数。为 0 时 <see cref="PredictedStepSpeed"/> 没有意义（别拿去比）。</summary>
    int PredictedSteps,
    float PredictedStepSpeed,
    float AuthoritativeStepSpeed,
    bool Starved,
    int BlendTicks)
{
    /// <summary>
    /// 诊断：**同一个 ack 区间**内"我自己按操作流走了多少"（上一份快照的 ack → 本份的 ack，
    /// 用我环里记的两个状态算）。
    ///
    /// 和 <see cref="ServerMove"/> 一起看就能分清失配的性质：
    ///   · 两者**不等**（差 ≈ err）⇒ 速度差（坡度/有效速度/少走一步）；
    ///   · 两者**接近**而 err 很大 ⇒ 同速不同向（贴障碍滑动/动态避让的模型差异）。
    /// </summary>
    public float ClientMove { get; init; }

    /// <summary>诊断：同一 ack 区间内**服务端**走了多少（两份快照的权威位置之差）。</summary>
    public float ServerMove { get; init; }

    /// <summary>诊断：这个区间跨了几条操作（ack 之差）。</summary>
    public int DriftOps { get; init; }

    /// <summary>诊断：客户端在这个区间里**实际走了几步**（应当等于 <see cref="DriftOps"/> 里的移动操作数）。</summary>
    public int ClientSteps { get; init; }
}

/// <summary>远端实体（别人）这一次采样发生了什么。</summary>
public enum RemoteSampleKind
{
    /// <summary>没有任何样本。</summary>
    Empty,

    /// <summary>在两个真实样本之间插值。</summary>
    Interpolated,

    /// <summary>播放时钟跑过了最新样本，按最后一段的真实速度有界外推。</summary>
    Extrapolated,

    /// <summary>外推到顶/不允许外推：冻结在最新样本，绝不倒退。</summary>
    Frozen,
}

/// <summary>远端实体一次采样的诊断。</summary>
public readonly record struct RemoteSampleReport(
    double PlayoutTick,
    long FromTick,
    long ToTick,
    float Alpha,
    RemoteSampleKind Kind,
    int Buffered,
    bool Starved,
    double SinceLastSampleTicks);
