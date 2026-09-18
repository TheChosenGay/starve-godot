namespace Starve.Netcode;

/// <summary>
/// 组件可调参数。全部有缺省值；换游戏/换 tick 频率只调这里，组件代码不动。
/// </summary>
public sealed record NetcodeConfig
{
    /// <summary>服务端一 tick 多少毫秒（我们 20Hz → 50）。</summary>
    public double TickMs { get; init; } = 50.0;

    /// <summary>别人渲染在过去多少 tick（拿延迟换平滑的旋钮）。</summary>
    public double InterpolationDelayTicks { get; init; } = 2.0;

    /// <summary>样本迟到时最多外推多少 tick；到顶冻结（绝不倒退）。</summary>
    public double MaxExtrapolationTicks { get; init; } = 1.0;

    /// <summary>小于这个误差视为噪声：直接对齐（跳这一点点看不见），不做平滑。
    /// 否则残差会被反复平滑 → 出现永久微抖。</summary>
    public float CorrectionDeadzone { get; init; } = 0.03f;

    /// <summary>超过这个距离就不再掩饰，直接贴（撞墙/被顶开/传送）。
    /// ⚠️ 判据是<b>本次的新失配</b>，不是"屏幕上累积的偏移"：累积那部分正是平滑要消化的东西，
    /// 拿它判定会把"延迟尖峰恢复后屏幕落后 2 格、而本次只差 0.38"误判成传送（实测一次 2 格突跳）。</summary>
    public float CorrectionSnapAbove { get; init; } = 1.5f;

    /// <summary>
    /// 走动时残差的收敛速度上限（单位/秒）。与走速同量级 →
    /// 最坏情况看起来只像"走得快了一点"，绝不会像瞬移。
    /// 过渡时长由它反推：<c>时长 = 残差 / 速度上限</c>，所以速度上限**永远成立**。
    /// </summary>
    public double MaxCorrectionSpeed { get; init; } = 5.0;

    /// <summary>
    /// 静止时的收敛速度上限（更大 = 收得更快）。
    /// 站着不动时屏幕上的唯一运动就是这段平移，最显眼，所以要比走动时收得快。
    /// </summary>
    public double MaxCorrectionSpeedStopped { get; init; } = 12.0;

    /// <summary>
    /// 过渡时长下限（ms）。**必须明显大于"一帧跨过的 tick 数"**：
    /// 一帧可能跨 1 个多 tick（帧长 16.7ms / tick 50ms 且相位会滑动），
    /// 若下限只有 1 个多 tick，过渡会在一帧内走完 —— 等于没有平滑（实测渲染一次跳 0.5 格）。
    /// 150ms = 3 tick，留出余量。
    /// </summary>
    public double CorrectionBlendMinMs { get; init; } = 150.0;


    /// <summary>
    /// 单次重放的 tick 上限。它必须 **≥ 预期最大单程延迟（tick）**，
    /// 否则客户端永远追不上服务端，每份快照都会退化成"直接贴"。
    /// 默认 20 tick = 1 秒单程（公网极端情况）；同时也是防时钟异常的硬保护。
    /// </summary>
    public int MaxReplayTicks { get; init; } = 20;

    /// <summary>时钟每次最多校正多少毫秒（防止相位跳变）。</summary>
    public double ClockMaxStepMs { get; init; } = 10.0;

    /// <summary>每次吃掉偏差的比例（比例 + 上限，收敛快但绝不跳）。</summary>
    public double ClockCorrectionRatio { get; init; } = 0.1;

    /// <summary>偏差大到这个量级才硬同步（服务端重启/坏数据；普通延迟抖动不该触发）。</summary>
    public double ClockResyncMs { get; init; } = 5000.0;

    /// <summary>多久没有权威数据算"断线"（抖动不算）。</summary>
    public double StallMs { get; init; } = 1000.0;

    /// <summary>是否允许外推；关掉则一律冻结在最新样本。</summary>
    public bool Extrapolate { get; init; } = true;

    /// <summary>一秒多少个 tick（= 1000/TickMs）。</summary>
    public double TicksPerSecond => 1000.0 / TickMs;

    /// <summary>一 tick 多少秒。</summary>
    public double TickSeconds => TickMs / 1000.0;

    /// <summary>
    /// 【序号锚定和解】每个 tick 都是一条**编号操作**（序号 = 我自己的 tick 编号），
    /// 并保存"执行完每条操作之后我自己的状态"（<see cref="OpHistoryCapacity"/> 条的小环）。
    ///
    /// 为什么必须这样：和解要比的是"**同一个操作前缀**之后的状态"。只要"一条操作 = 一个 tick"
    /// 在两边都成立，同一个序号在两边就对应同样的 tick 数 ⇒ 两个状态可以直接比，
    /// **完全不需要知道"我这条操作在服务端哪一 tick 生效"**（那是在途时间，发送时不可知）。
    /// 关掉时走旧路径（估计生效 tick + 在服务端 tick 轴上重放），保留兼容。
    /// </summary>
    public bool TickIndexedIntents { get; init; }

    /// <summary>序号锚定模式保存多少条"操作之后的状态"（= 能回看多少个 tick）。</summary>
    public int OpHistoryCapacity { get; init; } = 64;

    /// <summary>
    /// 【自己的角色】渲染延迟几个 tick —— 决定"tick 之间怎么补"。
    ///
    /// 背景：模拟只走**整 tick**（20Hz，每 50ms 一步），渲染却要按帧（~60fps）画。
    /// 一帧里"走完最后一个整 tick 之后剩下的那部分时间"就是**帧余量**（0~1 个 tick 的比例）。
    /// 用它把"整 tick 的位置"补成"当前这一刻的位置"，就是 tick 之间的插值/外推。
    ///
    /// <list type="bullet">
    /// <item><b>1.0（默认，插值）</b>：在**上一条 tick 状态 → 最新 tick 状态**之间插值，
    ///   即 <c>lerp(S(n-1), S(n), 帧余量)</c>。两个端点都是**已知的**，所以渲染位移严格线性、
    ///   永远不会"猜错了再回抽"；代价是画出来的位置比最新预测状态**晚约一个 tick（50ms）**。</item>
    /// <item><b>0.0（外推）</b>：从最新状态往前推 <c>S(n) + 帧余量 × 上一步位移</c>。
    ///   零额外延迟，但那是**对未来半个 tick 的猜测**：坡度变化、撞到东西、方向反转、
    ///   服务端校正都会让猜测落空 ⇒ 下一个整 tick 落地时把位置"抽回来"
    ///   （端上逐帧实测：直线行走 599 帧里 30 帧 ΔX&lt;0，最深倒退一整步）。</item>
    /// </list>
    ///
    /// 为什么默认插值：自己的角色也要能看（NPC 走的就是插值那条路，所以它们一直很顺）。
    /// 想要零延迟手感就用 0.0（或 0.5 折中：只外推半个 tick）。
    /// </summary>
    public double OwnRenderDelayTicks { get; init; } = 1.0;

    /// <summary>
    /// 每帧冗余上行几条操作（= <see cref="ClientSmoother{TState,TAction}.CollectUnackedOps"/> 的上限）。
    ///
    /// 批量构成 = **头部 1 条**（`ack+1`：服务端按序号连续消费，唯一能卡住它的就是这条）
    /// + **最新 N-1 条**（保证新操作发得出去，并让每条操作在"最新几条"期间被重发若干次 = 丢包覆盖）。
    ///
    /// 为什么是 4：3 时"头部 + 最新 2"比早先的"最新 3"少一个丢包覆盖名额，
    /// 仿真里 50% 丢包的极端档位会因此多几次跳过缺口；多带一条就恢复了覆盖，
    /// 而这几条是几十字节的小包，代价可以忽略。
    /// </summary>
    public int RedundantOps { get; init; } = 4;
}
