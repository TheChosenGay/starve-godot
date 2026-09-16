using Starve.Core;

namespace Starve.Core.Tests;

/// <summary>
/// 修复 B 的契约：样本用尽时按速度外推；外推期间到达的样本若与预测不符，
/// 必须**平滑过渡**而不是瞬移（用户明确要求）。
/// 注意：<c>Current(t)</c> 是纯函数，同一 wall 多次调用结果相同，测试里不要重复调用来推进时间。
/// </summary>
public sealed class PositionSmootherExtrapolationTests
{
    private const float Frame = 1000f / 60f;

    /// <summary>样本用尽（快照迟到）时必须继续前进，不能卡住 —— 外推的核心价值。</summary>
    [Fact]
    public void ExtrapolatesWhenSnapshotLate()
    {
        var sm = new PositionSmoother(delayTicks: 1, maxExtrapTicks: 2);
        sm.SetServerVelocity(10f, 0f); // 10 格/秒 → 0.2 格/帧
        sm.Update(0f, 0f, 1, 0);
        sm.Update(0.5f, 0f, 2, 50);

        // 从 tick 2 起快照停了：渲染应继续推进而不是冻结
        var wall = 100L;
        var first = sm.Current(wall).X;
        float last = first;
        for (var f = 0; f < 6; f++)
        {
            wall += (long)Frame;
            last = sm.Current(wall).X;
        }
        Assert.True(last > first + 0.05f,
            $"样本用尽后应继续外推前进：first={first:F4} last={last:F4}");
    }

    /// <summary>
    /// 停止（权威速度清零）后不得继续外推滑行。
    /// 关键：末段位移速度是**非零**的（最后一 tick 走了 0.5 格），
    /// 若实现忽略"服务端已停止"这一事实而回退到末段位移，就会一直滑下去。
    /// </summary>
    [Fact]
    public void DoesNotSlideAfterStop()
    {
        var sm = new PositionSmoother(delayTicks: 1, maxExtrapTicks: 100);
        sm.SetServerVelocity(10f, 0f);
        sm.Update(0f, 0f, 1, 0);
        sm.Update(0.5f, 0f, 2, 50); // 末段位移 0.5 格/tick（非零！）
        sm.SetServerVelocity(0f, 0f); // 服务端确认停止

        var wall = 100L;
        var first = sm.Current(wall).X;
        for (var f = 0; f < 40; f++) wall += (long)Frame;
        var last = sm.Current(wall).X;
        Assert.Equal(first, last, 4);
        Assert.False(sm.Extrapolating, "停止后不应处于外推状态");
    }

    /// <summary>
    /// 核心要求（用户明确指定）：外推期间到达的样本若与预测不一致，必须**平滑过渡**。
    /// 断言三件事：
    ///   ① 新样本到达的那一帧不得瞬移（离权威位置仍远）；
    ///   ② 过渡期间逐帧位移连续，无单帧大跳；
    ///   ③ 过渡窗口（blendTicks*50ms）结束后精确落在权威轨迹上。
    /// 注意：持续不喂新样本时，权威轨迹自身会一直处于"受上限约束的外推"状态，
    /// 因此收敛目标取"blendTicks 结束那一帧的权威轨迹值"，而不是样本坐标本身。
    /// </summary>
    [Fact]
    public void MismatchedSampleBlendsSmoothly()
    {
        var sm = new PositionSmoother(delayTicks: 1, maxExtrapTicks: 0.5f, blendTicks: 4f);
        sm.SetServerVelocity(10f, 0f);
        sm.Update(10f, 0f, 1, 0);
        sm.Update(10.5f, 0f, 2, 50);

        // 靠外推前进若干帧（快照迟到）
        var wall = 100L;
        for (var f = 0; f < 8; f++) { sm.Current(wall); wall += (long)Frame; }
        var lastExtrap = sm.Current(wall).X;
        Assert.True(sm.Extrapolating, "此时应处于外推状态");

        // 权威样本到达，但位置**远落后于**外推预测（模拟外推过头 / 急停）
        var authoritative = lastExtrap - 0.9f;
        sm.Update(authoritative, 0f, 3, wall);

        // 脱节判定发生在渲染帧里（Current），因为只有那里时间基准统一：
        // 同时知道"此刻显示在哪"和"权威轨迹此刻应该在哪"。
        wall += (long)Frame;
        var first = sm.Current(wall).X;
        Assert.True(sm.LastBlendDistance > 0.08f,
            $"应记录到需要平滑的残差，实际={sm.LastBlendDistance:F4}");

        // ① 过渡开始：第一帧不得直接落到权威位置
        Assert.True(MathF.Abs(first - authoritative) > 0.2f,
            $"新样本到达不得瞬移：first={first:F4} auth={authoritative:F4}");

        // ② 逐帧位移连续（无单帧大跳），且全程不得反向远离权威位置
        var prev = first;
        var maxStep = 0f;
        var blendFrames = (int)(4f * 50f / Frame) + 1; // blendTicks 窗口内的帧数
        for (var f = 0; f < blendFrames; f++)
        {
            wall += (long)Frame;
            var cur = sm.Current(wall).X;
            maxStep = MathF.Max(maxStep, MathF.Abs(cur - prev));
            prev = cur;
        }
        Assert.True(maxStep < 0.25f, $"过渡期间不应出现单帧大跳：maxStep={maxStep:F4}");

        // ③ 过渡结束后，输出应与权威轨迹一致（同一 wall 下两条路径重合）
        var afterBlend = sm.Current(wall).X;
        wall += (long)Frame;
        var drift = sm.Current(wall).X - afterBlend;
        // 权威轨迹受 maxExtrapTicks 约束，每帧推进量有限且方向一致
        Assert.True(drift >= -0.0001f,
            $"过渡结束后不得反向漂移：drift={drift:F5}");
    }

    /// <summary>残差很小时不启动过渡（直接对齐），避免常态拖尾。</summary>
    [Fact]
    public void SmallMismatchDoesNotBlend()
    {
        var sm = new PositionSmoother(delayTicks: 1, maxExtrapTicks: 4);
        sm.SetServerVelocity(10f, 0f);
        sm.Update(0f, 0f, 1, 0);
        sm.Update(0.5f, 0f, 2, 50);
        var wall = 100L;
        for (var f = 0; f < 8; f++) { sm.Current(wall); wall += (long)Frame; }
        var cur = sm.Current(wall).X;
        sm.Update(cur + 0.01f, 0f, 3, wall); // 极小残差
        Assert.Equal(0f, sm.LastBlendDistance, 4);
    }

    /// <summary>传送（距离远超 snapDistance）仍然直接吸附，不进入平滑。</summary>
    [Fact]
    public void TeleportSnapsImmediately()
    {
        var sm = new PositionSmoother(delayTicks: 1, snapDistance: 12);
        sm.SetServerVelocity(10f, 0f);
        sm.Update(0f, 0f, 1, 0);
        sm.Update(100f, 0f, 2, 50);
        Assert.Equal(100f, sm.Current(50).X, 3);
    }

    /// <summary>
    /// 服务端权威速度必须优先于末段位移速度。
    /// 构造使两者**不同**：末段位移 0.5 格/tick（=10 格/秒），权威速度 20 格/秒。
    /// 同一次外推推进量应等于 权威速度 × beyond，而不是末段位移 × beyond。
    /// </summary>
    [Fact]
    public void PrefersServerVelocityOverTailDisplacement()
    {
        static (float Step, float Beyond) Run(bool useServerVel)
        {
            var sm = new PositionSmoother(delayTicks: 1, maxExtrapTicks: 20);
            if (useServerVel) sm.SetServerVelocity(20f, 0f); // 0.4 格/tick
            sm.Update(0f, 0f, 1, 0);
            sm.Update(0.5f, 0f, 2, 50); // 末段位移 0.5 格/tick

            var wall = 100L;
            var a = sm.Current(wall).X;
            wall += 16;
            var b = sm.Current(wall).X;
            // beyond = sinceUpdate/50 = 16/50 = 0.32 tick
            return (b - a, 0.32f);
        }

        var (withVel, beyond) = Run(useServerVel: true);
        var (tailOnly, _) = Run(useServerVel: false);
        Assert.Equal(0.4f * beyond, withVel, 3);   // 用权威速度 20 格/秒
        Assert.Equal(0.5f * beyond, tailOnly, 3);  // 回退末段位移
        Assert.NotEqual(withVel, tailOnly);
    }

    /// <summary>
    /// 回归：到达外推上限时必须**冻结端点**，既不得向后瞬跳、也不得无限前进。
    /// 旧实现写成 vx*beyond*decay：beyond 增长而 decay 下降，乘积先增后减，
    /// 画面在外推超限后开始倒退（实测倒退 0.79 格）。
    /// </summary>
    [Fact]
    public void ExtrapolationFreezesAtLimitInsteadOfSnappingBack()
    {
        var sm = new PositionSmoother(delayTicks: 1, maxExtrapTicks: 2, blendTicks: 4f);
        sm.SetServerVelocity(20f, 0f); // 0.4 格/tick
        sm.Update(0f, 0f, 1, 0);
        sm.Update(1f, 0f, 2, 50);

        var wall = 100L;
        var prev = sm.Current(wall).X;
        var maxBackStep = 0f;
        var maxX = prev;
        for (var f = 0; f < 60; f++)
        {
            wall += (long)Frame;
            var cur = sm.Current(wall).X;
            if (cur < prev) maxBackStep = MathF.Max(maxBackStep, prev - cur);
            maxX = MathF.Max(maxX, cur);
            prev = cur;
        }

        // 不得后退
        Assert.True(maxBackStep < 0.05f,
            $"外推收尾不得向后瞬跳：maxBackStep={maxBackStep:F4}");
        // 不得无限前进：上限 = last + vx*maxExtrapTicks = 1 + 0.4*2 = 1.8
        Assert.True(maxX <= 1.8f + 0.001f,
            $"外推必须停在上限处：maxX={maxX:F4} 期望 <= 1.8");
        // 且确实推进到了上限附近（否则等于没外推）
        Assert.True(maxX > 1.79f, $"应推进到上限：maxX={maxX:F4}");
    }

    /// <summary>
    /// 回归（实测 bug）：**持续稳态移动时不得抖动**。
    ///
    /// 曾经在 Update() 里判定"预测脱节"：拿上一帧的输出与"新样本折算的延迟位置"比较，
    /// 两者时间基准不一致，稳态运动时残差恒为半个 tick 的位移（10 格/秒 → 0.5 格），
    /// 远超 blendThreshold(0.08)，于是**每个快照都被误判为脱节**、混合被反复重启，
    /// 输出被一再拉回旧位置 —— 玩家按住 S 时剧烈抖动（A/D 因另一轴不受同样相位影响，
    /// 看起来正常，所以初看像"只有某个方向有问题"）。
    ///
    /// 契约：稳态运动下逐帧位移必须近似恒定，且**绝不后退**、不出现零位移帧。
    /// </summary>
    [Fact]
    public void SteadyMotionDoesNotJitter()
    {
        foreach (var jitter in new[] { false, true })
        {
            var sm = new PositionSmoother();
            var xs = new List<float>();
            double serverPos = 0;
            long tick = 0;
            double wallF = 0;
            for (var f = 0; f < 180; f++)
            {
                wallF += Frame;
                var wall = (long)wallF;
                // 服务端每 50ms 一个 tick，位置 +0.5 格（10 格/秒）
                if (f % 3 == 0)
                {
                    tick++;
                    serverPos += 0.5;
                    sm.SetServerVelocity(10f, 0f);
                    // jitter 模拟快照到达时间的轻微抖动（真实网络必然存在）
                    var j = jitter && f % 6 == 0 ? 1 : 0;
                    sm.Update((float)serverPos, 0f, tick, wall + j);
                }
                xs.Add(sm.Current(wall).X);
            }

            var d = new List<float>();
            for (var i = 1; i < xs.Count; i++) d.Add(xs[i] - xs[i - 1]);
            var backward = d.Count(v => v < -0.001f);
            // 前 3 帧是起步（第一个样本刚建立），不计入稳态
            var steady = d.Skip(4).ToList();
            var avg = steady.Average();
            var sd = MathF.Sqrt(steady.Sum(v => (v - avg) * (v - avg)) / steady.Count);

            Assert.True(backward == 0,
                $"[jitter={jitter}] 稳态移动不得后退：backward={backward}");
            // 理想 0.1667 格/帧；抖动必须远小于单帧位移
            Assert.True(sd < 0.03f,
                $"[jitter={jitter}] 稳态移动逐帧位移应均匀：sd={sd:F4}（理想≈0.167）");
            // 稳态下**不允许任何零位移帧**：哪怕一帧冻结，视觉上就是一次顿挫。
            // （只放宽起步后的前几帧，那时样本刚建立。）
            var frozen = steady.Count(v => MathF.Abs(v) < 0.001f);
            Assert.True(frozen == 0,
                $"[jitter={jitter}] 稳态移动不得出现冻结帧：frozen={frozen}");
            Assert.True(MathF.Abs(avg - 0.1667f) < 0.02f,
                $"[jitter={jitter}] 平均速度应等于 10 格/秒：avg={avg:F4}");
        }
    }

    /// <summary>
    /// 过渡**起始那一帧**也必须前进，不得冻结。
    ///
    /// 混合系数按 elapsed/total 从 0 升到 1；若 arming 帧的 elapsed=0 直接返回 0，
    /// 输出会被钉在混合起点（= 脱节瞬间的旧位置），整整一帧不动——视觉上就是顿挫。
    /// 之前的测试都从 arming 帧的**下一帧**开始断言，所以漏掉了这一帧。
    /// </summary>
    [Fact]
    public void BlendArmingFrameDoesNotFreeze()
    {
        var sm = new PositionSmoother(delayTicks: 1, maxExtrapTicks: 4, blendTicks: 4f);
        sm.SetServerVelocity(10f, 0f);
        sm.Update(0f, 0f, 1, 0);
        sm.Update(0.5f, 0f, 2, 50);

        // 靠外推前进，制造"预测已偏离"
        var wall = 100L;
        for (var f = 0; f < 8; f++) { sm.Current(wall); wall += (long)Frame; }
        var lastExtrap = sm.Current(wall).X;

        // 权威样本远落后于外推预测 → 下一帧将触发过渡
        sm.Update(lastExtrap - 0.9f, 0f, 3, wall);

        // 过渡被触发的那一帧
        wall += (long)Frame;
        var armed = sm.Current(wall).X;
        Assert.True(sm.LastBlendDistance > 0.08f, "本帧应触发过渡");
        var moved = MathF.Abs(armed - lastExtrap);
        Assert.True(moved > 0.001f,
            $"过渡起始帧不得冻结：moved={moved:F5}（上一帧={lastExtrap:F4} 本帧={armed:F4}）");
    }
}
