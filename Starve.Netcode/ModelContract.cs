namespace Starve.Netcode;

/// <summary>
/// 预测模型的**契约校验器**。任何 <see cref="INetModel{TState,TAction}"/> 实现都应该在
/// 自己的测试里跑一遍 <c>AssertContract</c>。
///
/// 为什么需要它：组件的要求是"模型对**链**无状态"——玩家移动和 smoother 内部重放
/// 会调用**同一个模型实例**。这条约束光靠注释守不住：一个藏了跨步状态的模型
/// 在单链场景下看起来完全正常，只有在"两条链交替经过同一实例"时才露馅。
///
/// 精确的契约（三段）：
/// <list type="number">
/// <item><b>链状态全部在 <typeparamref name="TState"/> 里</b>：模型不得持有任何
///   跨步演化、且会影响下一次 <see cref="INetModel{TState,TAction}.Step"/> 结果的字段
///   （自身速度、路径游标、调用计数、上一次的输入、第一次见到的基准……）。</item>
/// <item><b>世界视图是允许的输入</b>：地形/静态障碍/动态邻居/速度档位这类
///   "由调用方整体替换的只读输入"可以持有；但它们必须在 <c>Step</c> 内部**不被演化**，
///   也不得随调用次数累积。注意这会让 <c>Step</c> 成为
///   <c>(state, action, dt, 当时的 world view)</c> 的函数 —— 世界视图变了，同样的
///   (state, action) 也会给出不同结果，这是**已知且接受**的近似
///   （重放用的是"现在"的邻居，不是"当时"的）。</item>
/// <item><b>不依赖调用顺序或次数</b>：同样的入参必须给同样的出参；
///   <c>Step</c> 必须原样回传 <c>baseVersion</c>。</item>
/// </list>
///
/// ⚠️ 第 3 条的版本回传是**诊断**不是**修复**：组件在版本对不上时会重算一次，
/// 而这个重算只对"无状态模型"成立 —— 如果模型真有内部状态，重算只会让它更错。
/// </summary>
public static class ModelContract
{
    /// <summary>
    /// 四条检查全跑一遍。**必须提供工厂**：期望值要在干净实例上算，
    /// 否则"把第一次见到的基准当世界原点"这类实例级泄漏抓不到
    /// （共享实例上第一条链会把自己的原点留下，之后两条链都用它，看着反而"一致"）。
    /// </summary>
    public static void AssertContract<TState, TAction>(
        INetModel<TState, TAction> sharedModel,
        Func<INetModel<TState, TAction>> fresh,
        TState baseA,
        TState baseB,
        IReadOnlyList<TAction> actions,
        double dtSeconds,
        uint baseVersion = 0,
        float tolerance = 1e-4f)
        where TState : struct
        where TAction : struct
    {
        var action = actions.Count > 0 ? actions[0] : default(TAction);
        AssertIdempotent(sharedModel, baseA, action, dtSeconds, baseVersion, tolerance);
        AssertVersionEchoed(sharedModel, baseA, action, dtSeconds, baseVersion);
        AssertChainStateless(sharedModel, fresh, baseA, baseB, actions, dtSeconds, baseVersion, tolerance);
        AssertQueriesArePure(sharedModel, fresh, baseA, actions, dtSeconds, baseVersion, tolerance);
    }

    /// <summary>
    /// <c>Distance</c> / <c>Lerp</c> 也必须无副作用：组件会在推进过程中调用它们
    /// （同跨度速度对比、误差分档、别人插值）。这里"边跑边插查询调用"，
    /// 结果必须与干净实例上不带查询地跑一致。
    /// </summary>
    public static void AssertQueriesArePure<TState, TAction>(
        INetModel<TState, TAction> sharedModel,
        Func<INetModel<TState, TAction>> fresh,
        TState baseState,
        IReadOnlyList<TAction> actions,
        double dtSeconds,
        uint baseVersion = 0,
        float tolerance = 1e-4f)
        where TState : struct
        where TAction : struct
    {
        var clean = Run(fresh(), baseState, actions, dtSeconds, baseVersion);

        var state = baseState;
        for (var i = 0; i < actions.Count; i++)
        {
            sharedModel.Distance(state, clean);
            sharedModel.Distance(clean, state);
            sharedModel.Lerp(state, clean, 0.5f);
            sharedModel.Lerp(clean, state, 0.5f);
            sharedModel.Step(ref state, actions[i], dtSeconds, baseVersion);
        }

        AssertClose(sharedModel, state, clean, tolerance,
            "在推进过程中插入 Distance/Lerp 调用后结果变了 —— 查询函数有副作用。");
    }

    /// <summary>
    /// <b>两条独立的链交替穿过同一个模型实例</b>，每条链的结果必须与"在干净实例上单独跑"一致。
    ///
    /// 这是唯一能抓住"模型藏了跨步状态"的检查。幂等检查抓不到某些形态
    /// （例如"把第一次见到的基准当世界原点"：两次同基准调用结果一样，但换一条链就错），
    /// 而不带工厂的做法也可能被顺序掩盖 —— 所以期望值必须来自干净实例。
    /// </summary>
    public static void AssertChainStateless<TState, TAction>(
        INetModel<TState, TAction> sharedModel,
        Func<INetModel<TState, TAction>> fresh,
        TState baseA,
        TState baseB,
        IReadOnlyList<TAction> actions,
        double dtSeconds,
        uint baseVersion = 0,
        float tolerance = 1e-4f)
        where TState : struct
        where TAction : struct
    {
        var soloA = Run(fresh(), baseA, actions, dtSeconds, baseVersion);
        var soloB = Run(fresh(), baseB, actions, dtSeconds, baseVersion);

        var mixedA = baseA;
        var mixedB = baseB;
        for (var i = 0; i < actions.Count; i++)
        {
            sharedModel.Step(ref mixedA, actions[i], dtSeconds, baseVersion);
            sharedModel.Step(ref mixedB, actions[i], dtSeconds, baseVersion);
        }

        AssertClose(sharedModel, mixedA, soloA, tolerance,
            "两条链交替经过同一模型实例后，链 A 与干净实例上单独跑的结果不一致 —— 模型里有跨步状态。");
        AssertClose(sharedModel, mixedB, soloB, tolerance,
            "两条链交替经过同一模型实例后，链 B 与干净实例上单独跑的结果不一致 —— 模型里有跨步状态。");
    }

    /// <summary>同样的入参跑两次必须给同样的出参（抓"依赖调用次数/上一次输入"的模型）。</summary>
    public static void AssertIdempotent<TState, TAction>(
        INetModel<TState, TAction> model,
        TState baseState,
        TAction action,
        double dtSeconds,
        uint baseVersion = 0,
        float tolerance = 1e-4f)
        where TState : struct
        where TAction : struct
    {
        var first = baseState;
        model.Step(ref first, action, dtSeconds, baseVersion);

        var second = baseState;
        model.Step(ref second, action, dtSeconds, baseVersion);

        AssertClose(model, first, second, tolerance,
            "同样的 (state, action) 两次调用结果不同 —— 模型依赖调用次数或上一次调用。");
    }

    /// <summary><c>Step</c> 必须原样回传 <c>baseVersion</c>（组件靠它识别"基于旧基准的预测"）。</summary>
    public static void AssertVersionEchoed<TState, TAction>(
        INetModel<TState, TAction> model,
        TState baseState,
        TAction action,
        double dtSeconds,
        uint baseVersion)
        where TState : struct
        where TAction : struct
    {
        var state = baseState;
        var used = model.Step(ref state, action, dtSeconds, baseVersion);
        if (used != baseVersion)
        {
            throw new InvalidOperationException(
                $"Step 没有原样回传 baseVersion：传入 {baseVersion}，回传 {used}。" +
                "纯实现直接 return baseVersion 即可；只有确实基于旧基准算出结果时才回传旧版本。");
        }
    }

    private static TState Run<TState, TAction>(
        INetModel<TState, TAction> model,
        TState start,
        IReadOnlyList<TAction> actions,
        double dtSeconds,
        uint baseVersion)
        where TState : struct
        where TAction : struct
    {
        var state = start;
        for (var i = 0; i < actions.Count; i++)
            model.Step(ref state, actions[i], dtSeconds, baseVersion);
        return state;
    }

    private static void AssertClose<TState, TAction>(
        INetModel<TState, TAction> model,
        TState actual,
        TState expected,
        float tolerance,
        string message)
        where TState : struct
        where TAction : struct
    {
        var distance = model.Distance(actual, expected);
        if (distance > tolerance)
            throw new InvalidOperationException($"{message}（差距 {distance}，容差 {tolerance}）");
    }
}
