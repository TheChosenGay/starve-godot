using Starve.Netcode;

namespace Starve.Netcode.Tests;

/// <summary>
/// 残差平滑：重放结果与"用户已经预测出去的显示位置"不一致时，怎么平滑过去。
///
/// 核心不变量：
/// <list type="number">
/// <item><b>接缝零跳变</b>：校正那一刻，渲染位置必须等于校正前屏幕上的位置；</item>
/// <item><b>必然收敛</b>：过渡结束时渲染位置**精确**等于模拟位置（不留残余 offset）；</item>
/// <item><b>速率有上限</b>：每一 tick 的渲染位移 ≤ 模拟步长 + 修正速度上限×dt
///   —— 所以再大的残差也不会像瞬移；</item>
/// <item><b>不回流</b>：平滑只影响渲染，模拟状态在快照后立刻就是权威值。</item>
/// </list>
/// </summary>
public sealed class CorrectionBlendTests
{
    private const long Tick0 = 1000;
    private const long LatencyMs = 100;

    private static long Now0 => Tick0 * 50 + LatencyMs;
    private static long WallOf(long tick) => tick * 50 + LatencyMs;

    private static FakeAction A(int dx) => new() { Dx = dx };

    private static NetSnapshot<FakeState> Snap(long tick, float x, ulong seq = 1, ulong epoch = 7) =>
        new(tick, seq, epoch, new FakeState { X = x }, false);

    private static ClientSmoother<FakeState, FakeAction> New(NetcodeConfig? config = null) =>
        new(new FakeModel { Speed = 10f }, config);

    /// <summary>走 5 个 tick（每 tick 0.5 格），到 X = 2.5。</summary>
    private static ClientSmoother<FakeState, FakeAction> WalkingTo5(NetcodeConfig? config = null)
    {
        var smoother = New(config);
        smoother.OnSnapshot(Snap(Tick0, 0f), Now0);
        smoother.SetIntent(1, 7, A(1), Now0);
        smoother.AdvanceTo(Tick0 + 5);
        return smoother;
    }

    [Fact]
    public void BlendStartsAtTheCurrentScreenPositionAndSimulationIsAlreadyAuthoritative()
    {
        var smoother = WalkingTo5();
        var seam = smoother.RenderedState.X;
        Assert.Equal(2.5f, seam, 4);

        var report = smoother.OnSnapshot(Snap(Tick0 + 5, 2.0f, seq: 2), WallOf(Tick0 + 5));

        Assert.Equal(CorrectionKind.Blended, report.Kind);
        Assert.True(report.BlendTicks > 0);
        Assert.Equal(seam, smoother.RenderedState.X, 4); // ★ 接缝零跳变
        Assert.Equal(2.0f, smoother.State.X, 4);          // ★ 模拟立刻是权威值
    }

    [Fact]
    public void BlendConvergesExactlyToTheSimulation()
    {
        var smoother = WalkingTo5();
        var report = smoother.OnSnapshot(Snap(Tick0 + 5, 2.0f, seq: 2), WallOf(Tick0 + 5));

        smoother.AdvanceTo(Tick0 + 5 + report.BlendTicks);

        Assert.False(smoother.Blending);
        Assert.Equal(0f, smoother.CorrectionBlendWeight, 4);
        Assert.Equal(smoother.State.X, smoother.RenderedState.X, 6); // 精确重合，不留残差
    }

    [Fact]
    public void BlendRateIsCappedSoItNeverLooksLikeATeleport()
    {
        var smoother = WalkingTo5();
        // 残差 1.4（< 1.5 的"直接贴"阈值）→ 应该被摊成多帧
        var report = smoother.OnSnapshot(Snap(Tick0 + 5, 1.1f, seq: 2), WallOf(Tick0 + 5));
        Assert.Equal(CorrectionKind.Blended, report.Kind);

        var simStep = 0.5f; // 10 格/秒 × 1 tick
        var cap = (float)(smoother.Config.MaxCorrectionSpeed * smoother.Config.TickSeconds);
        var previous = smoother.RenderedState.X;

        // 残差按常数速率衰减 ⇒ 每帧位移必须**双边**夹住：
        //   上界 = 模拟步长 + 修正速度上限（否则像瞬移）
        //   下界 = 模拟步长 − 修正速度上限（否则就是"停顿/被拖住"——正是我们要避免的抖动）
        for (var i = 1; i <= report.BlendTicks + 1; i++)
        {
            smoother.AdvanceTo(Tick0 + 5 + i);
            var current = smoother.RenderedState.X;
            var step = current - previous;
            Assert.True(step <= simStep + cap + 1e-3f,
                $"第 {i} 帧渲染位移 {step} 超过上界 {simStep + cap}（像瞬移）");
            Assert.True(step >= simStep - cap - 1e-3f,
                $"第 {i} 帧渲染位移 {step} 低于下界 {simStep - cap}（停顿/被拖住）");
            previous = current;
        }

        Assert.Equal(smoother.State.X, smoother.RenderedState.X, 6);
    }

    [Fact]
    public void BlendWorksWhileThePlayerKeepsWalking()
    {
        var smoother = WalkingTo5();
        var report = smoother.OnSnapshot(Snap(Tick0 + 5, 2.0f, seq: 2), WallOf(Tick0 + 5));
        Assert.Equal(CorrectionKind.Blended, report.Kind);

        var simStep = 0.5f;
        var cap = (float)(smoother.Config.MaxCorrectionSpeed * smoother.Config.TickSeconds);
        var positions = new List<float> { smoother.RenderedState.X };

        // 用户一直按着方向走，过渡期间渲染必须连续、且最终并入轨迹
        for (var i = 1; i <= report.BlendTicks + 2; i++)
        {
            smoother.AdvanceTo(Tick0 + 5 + i);
            positions.Add(smoother.RenderedState.X);
        }

        for (var i = 1; i < positions.Count; i++)
        {
            var step = MathF.Abs(positions[i] - positions[i - 1]);
            Assert.True(step <= simStep + cap + 1e-3f, $"第 {i} 帧位移 {step} 超上限");
        }

        // 残差为正（渲染在前）→ 过渡期间渲染走得更慢，让模拟追上来；绝不后退
        Assert.True(positions[1] >= positions[0] - 1e-4f);
        Assert.Equal(smoother.State.X, smoother.RenderedState.X, 6);
    }

    [Fact]
    public void SecondCorrectionDuringABlendDoesNotJump()
    {
        var smoother = WalkingTo5();
        smoother.OnSnapshot(Snap(Tick0 + 5, 2.0f, seq: 2), WallOf(Tick0 + 5)); // 残差 +0.5
        smoother.AdvanceTo(Tick0 + 6);                                        // 过渡进行到一半
        var mid = smoother.RenderedState.X;

        var second = smoother.OnSnapshot(Snap(Tick0 + 6, 1.5f, seq: 3), WallOf(Tick0 + 6));

        Assert.Equal(CorrectionKind.Blended, second.Kind);
        Assert.Equal(mid, smoother.RenderedState.X, 4); // ★ 重新起过渡也从当前屏幕位置开始
        Assert.Equal(1.5f, smoother.State.X, 4);
    }

    [Fact]
    public void DeadzoneDoesNotStartABlend()
    {
        var smoother = WalkingTo5();
        var report = smoother.OnSnapshot(Snap(Tick0 + 5, 2.49f, seq: 2), WallOf(Tick0 + 5));

        Assert.Equal(CorrectionKind.Deadzone, report.Kind);
        Assert.False(smoother.Blending);
        Assert.Equal(smoother.State.X, smoother.RenderedState.X, 6);
    }

    [Fact]
    public void BigErrorSnapsInsteadOfBlending()
    {
        var smoother = WalkingTo5();
        var report = smoother.OnSnapshot(Snap(Tick0 + 5, 0.5f, seq: 2), WallOf(Tick0 + 5));

        Assert.Equal(CorrectionKind.Snapped, report.Kind); // 差 2.0 ≥ 1.5：撞墙/传送，不掩饰
        Assert.False(smoother.Blending);
        Assert.Equal(0.5f, smoother.RenderedState.X, 4);
    }

    [Fact]
    public void BlendNeverFeedsBackIntoTheSimulation()
    {
        var smoother = WalkingTo5();
        smoother.OnSnapshot(Snap(Tick0 + 5, 2.0f, seq: 2), WallOf(Tick0 + 5));
        smoother.AdvanceTo(Tick0 + 6);

        // 模拟就是"权威 + 纯预测"：2.0 + 0.5 = 2.5，与残差无关
        Assert.Equal(2.5f, smoother.State.X, 4);
        // 渲染还在过渡中间 → 两者不同，证明确实是两层
        Assert.NotEqual(smoother.State.X, smoother.RenderedState.X);

        // 过渡下限 150ms = 3 tick：从快照那一 tick 起要走满 3 tick 才收敛，
        // 所以这里推到 Tick0+8 才是"过渡结束"的时刻（+7 时还剩 1 tick）。
        smoother.AdvanceTo(Tick0 + 8);
        Assert.Equal(3.5f, smoother.State.X, 4);
        Assert.Equal(3.5f, smoother.RenderedState.X, 4);
    }

    [Fact]
    public void ResidualAndRemainingTicksAreExposedForCustomPresentation()
    {
        // 业务想自己做表现层平滑时的逃生口：Residual + 剩余 tick 都在。
        var smoother = WalkingTo5();
        var report = smoother.OnSnapshot(Snap(Tick0 + 5, 2.0f, seq: 2), WallOf(Tick0 + 5));

        Assert.Equal(CorrectionKind.Blended, report.Kind);
        Assert.Equal(report.BlendTicks, smoother.BlendTicksTotal);
        Assert.Equal(report.BlendTicks, smoother.BlendTicksRemaining);
        Assert.Equal(1f, smoother.CorrectionBlendWeight, 4);

        // 自己按 State + k × Residual 算，与组件的 RenderedState 完全一致
        var custom = smoother.State.X + 1f * smoother.Residual.X;
        Assert.Equal(smoother.RenderedState.X, custom, 5);

        smoother.AdvanceTo(Tick0 + 8);
        Assert.False(smoother.Blending);
        Assert.Equal(0, smoother.BlendTicksRemaining);
    }

    [Fact]
    public void StoppedCorrectionIsFasterThanWhileWalking()
    {
        var walking = WalkingTo5();
        var walkReport = walking.OnSnapshot(Snap(Tick0 + 5, 1.1f, seq: 2), WallOf(Tick0 + 5));

        var stopped = New();
        stopped.OnSnapshot(Snap(Tick0, 0f), Now0);
        stopped.SetIntent(1, 7, A(0), Now0); // 松手：预测速度 0 → 判定为静止
        stopped.AdvanceTo(Tick0 + 5);
        var stopReport = stopped.OnSnapshot(Snap(Tick0 + 5, 1.4f, seq: 2), WallOf(Tick0 + 5));

        Assert.Equal(CorrectionKind.Blended, walkReport.Kind);
        Assert.Equal(CorrectionKind.Blended, stopReport.Kind);
        Assert.True(stopReport.BlendTicks < walkReport.BlendTicks,
            $"静止应收得更快：静止 {stopReport.BlendTicks} tick vs 走动 {walkReport.BlendTicks} tick");
    }

    [Fact]
    public void StoppedBlendAlsoRespectsTheSpeedCap()
    {
        var stopped = New();
        stopped.OnSnapshot(Snap(Tick0, 0f), Now0);
        stopped.SetIntent(1, 7, A(0), Now0);
        stopped.AdvanceTo(Tick0 + 5);

        var report = stopped.OnSnapshot(Snap(Tick0 + 5, 1.4f, seq: 2), WallOf(Tick0 + 5));
        Assert.Equal(CorrectionKind.Blended, report.Kind);

        var cap = (float)(stopped.Config.MaxCorrectionSpeedStopped * stopped.Config.TickSeconds);
        var previous = stopped.RenderedState.X;
        for (var i = 1; i <= report.BlendTicks + 1; i++)
        {
            stopped.AdvanceTo(Tick0 + 5 + i);
            var current = stopped.RenderedState.X;
            var step = current - previous;
            Assert.True(step <= cap + 1e-3f, $"静止前进过快：第 {i} 帧 {step} > {cap}");
            Assert.True(step >= -cap - 1e-3f, $"静止回退过快：第 {i} 帧 {step} < {-cap}");
            previous = current;
        }
    }
}
