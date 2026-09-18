using Starve.Netcode;

namespace Starve.Netcode.Tests;

/// <summary>
/// 一维匀速 + 可选"墙"的假状态。存在意义：**组件必须能用一个与游戏无关的玩具模型
/// 跑通全部机制** —— 需要真实游戏才能测，就说明切分没切干净。
/// </summary>
public struct FakeState
{
    public float X;
    public int Steps;
}

public struct FakeAction
{
    public int Dx;
}

public sealed class FakeModel : INetModel<FakeState, FakeAction>
{
    /// <summary>格/秒。</summary>
    public float Speed { get; set; } = 10f;

    /// <summary>非空时不许越过这条线（模拟撞墙：位移被截断）。</summary>
    public float? WallX { get; set; }

    /// <summary>接下来 N 次 Step 故意回传"过期版本"，模拟基于重放前旧基准算出的预测。</summary>
    public int StaleReturns { get; set; }

    public int StepCalls { get; private set; }

    public uint Step(ref FakeState state, in FakeAction action, double dtSeconds, uint baseVersion)
    {
        StepCalls++;
        var next = state.X + action.Dx * Speed * (float)dtSeconds;
        if (WallX is { } wall)
        {
            if (action.Dx > 0) next = MathF.Min(next, wall);
            else if (action.Dx < 0) next = MathF.Max(next, wall);
        }

        state.X = next;
        state.Steps++;

        if (StaleReturns > 0)
        {
            StaleReturns--;
            return baseVersion - 1; // 故意对不上版本
        }

        return baseVersion;
    }

    public float Distance(in FakeState a, in FakeState b) => MathF.Abs(a.X - b.X);

    /// <summary>位移只表达"可平移的那部分"（位置）；Steps 是诊断计数，不参与。</summary>
    public FakeState Delta(in FakeState from, in FakeState to) =>
        new() { X = to.X - from.X };

    public FakeState AddDelta(in FakeState state, in FakeState delta, float k) =>
        new() { X = state.X + k * delta.X, Steps = state.Steps };
}
