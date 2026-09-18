using Starve.Netcode;

namespace Starve.Netcode.Tests;

/// <summary>
/// 量的是**玩家按一下方向之后，他自己的预测链条多久才开始转** ——
/// 也就是"输入延迟"，和"预测位置误差"完全是两回事（所以之前的误差测试全都量不到它）。
///
/// 当前实现的口径：输入的"生效 tick"被标成**服务端大概在哪一 tick 用上它**
/// （= 按下时的时钟 tick + 往返估计），而链条头只推到"服务端现在"（= 时钟 tick + 单程）。
/// 两者相差正好**一个单程** ⇒ 自己的转弯要等一个单程才出现在自己的屏幕上。
///
/// 这个选择不是 bug，是取舍：链条与服务端**逐 tick 对齐** ⇒ 转弯几乎不产生和解
/// （恒定意图下误差恒为 0），代价就是这一条输入延迟。
/// 另一条路是"立刻用最新输入预测（零输入延迟）+ 用输入序号锚定和解"，
/// 那样就不需要知道它在服务端是哪一 tick 生效 —— 见 client-pre-pic.md §13。
/// 本用例把这个量钉住：换成序号锚定后，它必须掉到 ~0（那时改这个断言）。
/// </summary>
public sealed class InputResponseLatencyTests
{
    private const long Tick0 = 1000;
    private const ulong Epoch = 7;

    private static long WallOf(double tick) => (long)Math.Round(tick * 50.0);
    private static FakeAction A(int dx) => new() { Dx = dx };

    [Fact]
    public void OwnTurnWaitsForTheEstimatedOneWayDelayInTheCurrentDesign()
    {
        var s = new ClientSmoother<FakeState, FakeAction>(
            new FakeModel { Speed = 10f }, new NetcodeConfig { ClockMaxStepMs = 0 });
        s.OnSnapshot(new NetSnapshot<FakeState>(Tick0, 0, Epoch, new FakeState { X = 0 }, false), WallOf(Tick0));

        // 用标定事件把单程估计爬到一个真实公网量级（每次快照 +0.5 tick）
        ulong seq = 0;
        var tick = Tick0;
        for (var i = 0; i < 32; i++)
        {
            seq++;
            tick += 1;
            s.SetIntent(seq, Epoch, A(1), WallOf(tick));
            var snapTick = tick + 16;                    // observed = 16/2 = 8 tick
            s.OnSnapshot(
                new NetSnapshot<FakeState>(snapTick, seq, Epoch, new FakeState { X = (snapTick - Tick0) * 0.5f }, false),
                WallOf(snapTick));
        }

        var oneWay = s.EstimatedOneWayTicks;
        Assert.True(oneWay > 5, $"测试自身有问题：单程估计只有 {oneWay:F2} tick");

        // 玩家在这一刻按下反向
        var pressWall = WallOf(tick + 16);
        var head = s.StateTick;
        var label = s.Clock.TickAt(pressWall) + s.EstimatedInputDelayTicks;
        seq++;
        s.SetIntent(seq, Epoch, A(-1), pressWall);

        var waiting = label - head;
        Console.WriteLine($"[输入延迟] 单程 {oneWay:F2} tick（{oneWay * 50:F0}ms）→ " +
                          $"按下后要等 {waiting:F1} tick（{waiting * 50:F0}ms）自己的预测才开始反向");

        // 实证：逐 tick 推进，看哪一 tick 真的开始往回走
        var previous = s.State.X;
        var actual = -1;
        for (var i = 1; i <= 40 && actual < 0; i++)
        {
            s.AdvanceTo(s.StateTick + 1);
            if (s.State.X < previous - 1e-4f) actual = i;
            previous = s.State.X;
        }

        Assert.True(actual > 0, "一直没反向？");
        Console.WriteLine($"[输入延迟] 实证：推进 {actual} tick 后反向");
        // 当前设计：等待时间 ≈ 一个单程（≤1 tick 的差来自 StateTick 取整 + 一帧零头）。
        // 换成"立刻应用 + 序号锚定"后，这两个断言必须改成 ≈0。
        Assert.True(Math.Abs(waiting - oneWay) <= 1.0,
            $"等待 {waiting:F1} tick 与单程估计 {oneWay:F2} 不符（当前设计应当≈单程）");
        Assert.True(Math.Abs(actual - waiting) <= 2.0, $"实证 {actual} 与推算 {waiting:F1} 不符");
    }
}
