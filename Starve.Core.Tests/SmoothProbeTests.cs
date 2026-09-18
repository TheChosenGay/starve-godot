using Starve.Core;

namespace Starve.Core.Tests;

// 诊断：模拟"服务端 20Hz 快照 + 客户端 60Hz 渲染"，
// 观察 PositionSmoother 输出的位置序列是否平滑。
public sealed class SmoothProbeTests
{
    [Fact]
    public void ProbeInterpolationSmoothness()
    {
        var sm = new PositionSmoother();
        // 实体以 10 格/秒 向右移动，服务端 20Hz 更新位置（每 50ms +0.5 格）
        double serverX = 0;
        long tick = 0;
        long wall = 0;

        var outputs = new List<(long Wall, float X)>();
        // 客户端 60Hz 渲染
        for (var frame = 0; frame < 120; frame++)
        {
            wall = (long)(frame * 1000.0 / 60.0);
            // 服务端每 50ms 推一次快照
            if (frame % 3 == 0)
            {
                tick += 1;
                serverX += 0.5;
                sm.Update((float)serverX, 0f, tick, wall);
            }
            outputs.Add((wall, sm.Current(wall).X));
        }

        // 计算逐帧位移，看是否均匀
        var deltas = new List<float>();
        for (var i = 1; i < outputs.Count; i++)
            deltas.Add(outputs[i].X - outputs[i - 1].X);

        var min = deltas.Min();
        var max = deltas.Max();
        var avg = deltas.Average();
        Console.WriteLine($"PROBE 输出 {outputs.Count} 帧, 位移 min={min:F4} max={max:F4} avg={avg:F4}");
        Console.WriteLine($"PROBE 抖动比 max/min = {(min > 0 ? max / min : float.PositiveInfinity):F2}");
        Console.WriteLine("PROBE 前24帧位移: " + string.Join(" ", deltas.Take(24).Select(d => d.ToString("F3"))));
        var zeros = deltas.Count(d => d < 0.001f);
        Console.WriteLine($"PROBE 零位移帧数 = {zeros}/{deltas.Count}");
    }
}
