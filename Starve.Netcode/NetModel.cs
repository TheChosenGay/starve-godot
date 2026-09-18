namespace Starve.Netcode;

/// <summary>
/// 状态空间的**代数**：两个状态有多远、如何相减、如何按比例施加位移。
///
/// 为什么需要它（而不是只给一个 <c>Lerp</c>）：残差平滑要做的是
/// <c>render = sim + k × 残差</c>，其中 k 从 1 衰减到 0。
/// 用"在 sim 和旧显示位置之间插值"来表达是**错的**：模拟一直在前进、起点固定，
/// 两者交叉时插值会停顿甚至倒退（实测一帧零位移、下一帧双倍位移）。
/// 只有"把残差按比例加到当前位置上"才是常数速率的衰减，既不卡也不倒退。
/// </summary>
public interface IStateSpace<TState> where TState : struct
{
    /// <summary>两状态之间的距离（单位与各阈值一致）。</summary>
    float Distance(in TState a, in TState b);

    /// <summary>位移：<c>to - from</c>。只表达"可平移的那部分"（位置/子格），速度之类不参与。</summary>
    TState Delta(in TState from, in TState to);

    /// <summary>按比例施加位移：<c>state + k × delta</c>。k=0 原样返回，k=1 平移过去。</summary>
    TState AddDelta(in TState state, in TState delta, float k);

    /// <summary>
    /// 线性插值，<b>必须支持 t &gt; 1</b>（沿 a→b 继续往外走，组件靠它做外推）。
    /// 默认实现用 <see cref="Delta"/>/<see cref="AddDelta"/>；
    /// 朝向之类的非线性状态可以覆写（例如走最短角）。
    /// </summary>
    TState Lerp(in TState a, in TState b, float t) => AddDelta(a, Delta(a, b), t);
}

/// <summary>
/// 游戏侧必须提供的"网络模型"：单步推进 + 状态空间代数。这是组件与游戏之间**唯一**的耦合点。
///
/// 两条硬约束：
/// <list type="number">
/// <item>
///   <b>对链无状态</b>：凡是要跨步保留的可变状态，必须放进 <typeparamref name="TState"/>
///   （例如 ORCA 的自身速度）。否则重放到一半被放弃时模型内部会留在中间状态，
///   下一次重放不可复现。世界查询（地形、静态障碍、动态邻居）可以是外部注入的
///   只读输入，但**不得在 <c>Step</c> 内部被演化**，也不得随调用次数累积。
///   <br/>⚠️ 世界视图允许持有 ⇒ <c>Step</c> 实际是
///   <c>(state, action, dt, 当时的worldView)</c> 的函数：世界视图变了，同样的
///   (state, action) 也会给出不同结果。这是**已知且接受**的近似
///   （重放用的是"现在"的邻居，不是"当时"的），偏差由和解死区承担。
/// </item>
/// <item>
///   <b>只做单步</b>（给定基准 + 操作 → 下一步）。链式推进由组件驱动；
///   不要一次算多步，否则接口语义要改成"给我 base + 输入时间线"。
/// </item>
/// </list>
///
/// 用 <see cref="ModelContract"/> 在自己的测试里校验上面这些约束。
/// </summary>
public interface INetModel<TState, TAction> : IStateSpace<TState>
    where TState : struct
    where TAction : struct
{
    /// <summary>
    /// 从 <paramref name="state"/> 推进一步（dt 固定 = 一个 tick），并**原样回传**
    /// <paramref name="baseVersion"/>。
    ///
    /// 纯实现直接 <c>return baseVersion;</c> 即可。之所以要有这个回传：组件用它检测
    /// "这次预测是基于重放前的旧基准算出来的"——那种结果必须丢弃并重算。
    /// 若实现内部确实存了跨步状态（不推荐），且基准已过期，就回传它所依据的旧版本。
    /// </summary>
    uint Step(ref TState state, in TAction action, double dtSeconds, uint baseVersion);
}
