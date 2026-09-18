using Starve.Core;
namespace Starve.Core.Tests;

// 复现服务端 M7 连续位移 + 20Hz 快照 + 60Hz 渲染。
// 场景A：每 tick 都下发（修 A 之后）
// 场景B：只在跨格时下发（修 A 之前）——证明 bug
public sealed class SmoothProbe2Tests
{
    static List<float> Run(bool everyTick, float delayTicks, float maxExtrap, int frames)
    {
        var sm = new PositionSmoother(delayTicks, maxExtrap);
        var outv = new List<float>();
        float serverX = 0f;
        long tick = 0;
        long lastSentTick = -999;
        float lastSentX = float.NaN;
        for (var frame = 0; frame < frames; frame++)
        {
            var wall = (long)(frame * 1000.0 / 60.0);
            if (frame % 3 == 0)
            {
                tick += 1;
                serverX += 0.5f; // 10 格/秒 @ 20Hz
                var crossed = (int)MathF.Floor(serverX) != (int)MathF.Floor(serverX - 0.5f);
                var send = everyTick || crossed || float.IsNaN(lastSentX);
                if (send) { sm.Update(serverX, 0f, tick, wall); lastSentTick = tick; lastSentX = serverX; }
            }
            outv.Add(sm.Current(wall).X);
        }
        return outv;
    }

    static void Report(string name, List<float> xs)
    {
        var d = new List<float>();
        for (var i = 1; i < xs.Count; i++) d.Add(xs[i] - xs[i-1]);
        var zeros = d.Count(v => v < 0.0005f);
        var min = d.Min(); var max = d.Max(); var avg = d.Average();
        var varSum = d.Sum(v => (v-avg)*(v-avg)) / d.Count;
        Console.WriteLine($"PROBE2 {name}: min={min:F4} max={max:F4} avg={avg:F4} jitter(max/min)={(min>0?max/min:999):F2} zeros={zeros}/{d.Count} sd={MathF.Sqrt(varSum):F4}");
    }

    [Fact]
    public void Compare()
    {
        Report("A 每tick下发 d=1 e=1", Run(true, 1, 1, 180));
        Report("B 跨格才下发 d=1 e=1", Run(false, 1, 1, 180));
        Report("B 跨格才下发 d=1 e=3", Run(false, 1, 3, 180));
    }
}
