using Starve.Core;

namespace Starve.Core.Tests;

// 回归：PositionSmoother 的虚拟 tick 必须**与 tick 绝对值无关**。
//
// 真实 bug（2026-09）：虚拟时钟写成
//     var dt = _latestTick + sinceUpdate / 50f - _delayTicks;   // ← float
// 而 tick 是**服务端累计 tick**（存档会把它带到几十万甚至上百万）。
// float 只有 24 bit 有效位：
//     tick ≈ 7.2e5 → ULP = 0.0625 tick = 3.125ms
//     tick ≈ 1.1e6 → ULP = 0.125  tick = 6.25ms
//     tick ≈ 2.2e6 → ULP = 0.25   tick = 12.5ms
// 于是"虚拟 tick"只能取量化台阶，逐帧位移变成阶梯：帧率再高也一顿一顿，
// 并且**随服务器运行时间持续变坏**（存档里 tick 会一直涨）。
// 典型现象：跑了几小时后"玩家和生物移动开始卡"，但 FPS 一直很高。
//
// 下面这组断言在修复前是失败的（2.2e6 处 sd 是 0 处的 3.2 倍）。
public sealed class TickPrecisionProbeTests
{
    static List<float> Run(long tickBase, int frames, int fps)
    {
        var sm = new PositionSmoother();
        var outs = new List<float>();
        double serverX = 0;
        var framesPerTick = fps / 20.0;
        for (var frame = 0; frame < frames; frame++)
        {
            // 每帧长度按整数 ms 取整，贴合 Time.GetTicksMsec() 的真实粒度。
            var wall = (long)Math.Round(frame * 1000.0 / fps);
            if (Math.Abs(frame % framesPerTick) < 0.5)
            {
                serverX += 0.5; // 10 格/秒 @ 20Hz
                sm.Update((float)serverX, 0f, tickBase + (long)(frame / framesPerTick), wall);
            }
            outs.Add(sm.Current(wall).X);
        }
        return outs;
    }

    static float DisplacementSd(List<float> xs)
    {
        var d = new List<float>();
        for (var i = 1; i < xs.Count; i++) d.Add(xs[i] - xs[i - 1]);
        var avg = d.Average();
        return MathF.Sqrt(d.Sum(v => (v - avg) * (v - avg)) / d.Count);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    public void SmoothnessIsIndependentOfTickMagnitude(int fps)
    {
        var baseline = DisplacementSd(Run(0, fps * 4, fps));
        long[] bases = [100_000, 500_000, 719_892, 1_100_000, 2_200_000, 5_000_000];
        Console.WriteLine($"fps={fps} baselineSd={baseline:F5}");
        foreach (var b in bases)
        {
            var sd = DisplacementSd(Run(b, fps * 4, fps));
            Console.WriteLine($"  tickBase={b,9}  sd={sd:F5}  ratio={sd / baseline:F3}");
            // 修复前：719892 → 1.24×，1100000 → 1.81×，2200000 → 3.21×
            Assert.True(sd <= baseline * 1.05f,
                $"tick={b} 的逐帧位移抖动 {sd:F5} 明显高于基线 {baseline:F5}（{sd / baseline:F2}×）" +
                "——虚拟 tick 又回到低精度了（见本文件顶部注释）。");
        }
    }

    [Fact]
    public void FloatVirtualClockWouldQuantizeAtLargeTicks()
    {
        // 说明性：float 在 tick 量级上的 ULP，就是修复前虚拟时钟的台阶宽度。
        // 这个测试同时是"为什么必须 double"的活文档。
        var ulpAt720k = MathF.BitIncrement(719_892f) - 719_892f;
        var ulpAt2m = MathF.BitIncrement(2_200_000f) - 2_200_000f;
        Assert.Equal(0.0625f, ulpAt720k, 6);
        Assert.Equal(0.25f, ulpAt2m, 6);
        // 0.0625 tick = 3.125ms；120FPS 每帧只前进 8.333ms = 0.167 tick，
        // 量化台阶占每帧的 37.5% —— 所以刷新率越高越明显。
        Assert.True(ulpAt720k / 0.16667f > 0.3f);
    }
}
