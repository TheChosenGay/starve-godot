using Starve.Core;

namespace Starve.Core.Tests;

// 客户端预测 / 服务端和解的**可测判据**。
//
// 设计要点：判断"预测准不准"不能拿"本地现在"比"服务端过去"——那里面天然含
// 网络领先量（≈ v × 往返延迟），会把延迟误判成失配。正确判据是
// **同一 tick 跨度内**：本地预测走了多少 vs 服务端走了多少（ReconcileTrace 的 Step*）。
//
// 这组测试是重构（输入历史 + 重放）的验收基线：重构后 DriftSpeed 必须恒为 0。
public sealed class ReconcileTraceTests
{
    private static OwnMovementSim Sim(float speed = 10f)
    {
        var sim = new OwnMovementSim((_, _) => true);
        sim.SetSpeed(speed);
        sim.SetIntent(1, 0);
        sim.SnapTo(0f, 0f);
        return sim;
    }

    [Fact]
    public void DriftIsZeroWhenPredictionMatchesServerDespiteLargeLead()
    {
        var sim = Sim();
        long tick = 500;
        var serverX = -1.0f; // 服务端位置落后 1 格 = 网络领先，不是失配

        tick++;
        serverX += 0.5f;
        sim.FeedServerMotion(10f, 0f, 10f, 1, 0, 0);
        sim.Reconcile(serverX, 0f, false, tick);

        for (var i = 0; i < 40; i++)
        {
            sim.Tick(50f);          // 本地预测：10 格/秒 → 每 tick 0.5 格
            tick++;
            serverX += 0.5f;        // 服务端实际也每 tick 0.5 格
            sim.FeedServerMotion(10f, 0f, 10f, 1, 0, 0);
            sim.Reconcile(serverX, 0f, false, tick);
        }

        var t = sim.LastReconcile;
        Assert.True(t.HasStep);
        Assert.Equal(1, t.StepTicks);
        Assert.Equal(10f, t.ServerStepSpeed, 2);
        Assert.Equal(10f, t.LocalStepSpeed, 2);
        // 核心断言：领先量存在（err 大），但**预测速度没有偏差**。
        Assert.Equal(0f, t.DriftSpeed, 2);
        Assert.Equal(0f, t.DriftX, 2);
        Assert.True(t.Err > 0.3f, $"err 应反映领先量，实际 {t.Err}");
    }

    [Fact]
    public void DriftReportsPredictionSpeedError()
    {
        var sim = Sim(speed: 10f); // 本地按 10 格/秒预测
        long tick = 900;
        var serverX = 0f;

        sim.FeedServerMotion(8f, 0f, 8f, 1, 0, 0);
        tick++;
        sim.Reconcile(serverX, 0f, false, tick);

        for (var i = 0; i < 10; i++)
        {
            sim.Tick(50f);          // 本地 0.5 格
            tick++;
            serverX += 0.4f;        // 服务端 8 格/秒 → 0.4 格
            sim.FeedServerMotion(8f, 0f, 8f, 1, 0, 0);
            sim.Reconcile(serverX, 0f, false, tick);
        }

        var t = sim.LastReconcile;
        Assert.Equal(8f, t.ServerStepSpeed, 2);
        Assert.Equal(10f, t.LocalStepSpeed, 2);
        Assert.Equal(2f, t.DriftSpeed, 2);
        Assert.Equal(2f, t.DriftX, 2);
    }

    [Fact]
    public void DeclaredVelocityIsLoggedSeparatelyFromActualStep()
    {
        // 服务端"声明"10 格/秒，但实际一步没走（贴岸）——两个字段必须能分辨。
        var sim = Sim();
        long tick = 1200;
        sim.FeedServerMotion(10f, 0f, 10f, 1, 0, 0);
        tick++;
        sim.Reconcile(5f, 5f, false, tick);

        for (var i = 0; i < 5; i++)
        {
            sim.Tick(50f);          // 本地能走（客户端这侧没有岸）
            tick++;
            sim.FeedServerMotion(10f, 0f, 10f, 1, 0, 0);
            sim.Reconcile(5f, 5f, false, tick); // 服务端位置纹丝不动
        }

        var t = sim.LastReconcile;
        Assert.Equal(10f, t.ServerVelSpeed, 2);  // 声明的速度
        Assert.Equal(0f, t.ServerStepSpeed, 2);  // 实际位移 = 0
        Assert.Equal(10f, t.LocalStepSpeed, 2);  // 本地照常预测前进
        Assert.Equal(10f, t.DriftSpeed, 2);      // 于是预测速度"偏差"10
    }

    [Fact]
    public void RepeatedTickIsReportedAsStale()
    {
        var sim = Sim();
        long tick = 2000;
        sim.FeedServerMotion(10f, 0f, 10f, 1, 0, 0);
        sim.Reconcile(0f, 0f, false, ++tick);
        sim.Tick(50f);
        sim.FeedServerMotion(10f, 0f, 10f, 1, 0, 0);
        sim.Reconcile(0f, 0f, false, tick); // 同一个 tick 再来一次

        Assert.Equal(OwnMovementSim.ReconcileDecision.Stale, sim.LastReconcile.Decision);
    }

    [Fact]
    public void DecisionsAreReported()
    {
        // 注意：不要先 SnapTo——那会把首帧变成"已初始化"，InitialSnap 就走不到了。
        var sim = new OwnMovementSim((_, _) => true);
        sim.SetSpeed(10f);
        sim.SetIntent(1, 0);
        long tick = 3000;

        sim.FeedServerMotion(0f, 0f, 10f, 0, 0, 0);
        sim.Reconcile(4f, 4f, false, ++tick);
        Assert.Equal(OwnMovementSim.ReconcileDecision.InitialSnap, sim.LastReconcile.Decision);

        // 误差落在 (0.75, 4] → Soft（本地走两 tick，服务端不动 → 差 1.0 格）
        sim.Tick(50f);
        sim.Tick(50f);
        sim.FeedServerMotion(10f, 0f, 10f, 1, 0, 0);
        sim.Reconcile(4f, 4f, false, ++tick);
        Assert.Equal(OwnMovementSim.ReconcileDecision.Soft, sim.LastReconcile.Decision);

        // 误差 > 4 格 → HardSnap
        sim.FeedServerMotion(10f, 0f, 10f, 1, 0, 0);
        sim.Reconcile(40f, 40f, false, ++tick);
        Assert.Equal(OwnMovementSim.ReconcileDecision.HardSnap, sim.LastReconcile.Decision);

        // 服务端确认停止、误差 ≤4 → StopSnap（当前实现是一次到位吸附）
        sim.FeedServerMotion(0f, 0f, 10f, 0, 0, 0);
        sim.Reconcile(41f, 41f, serverStopped: true, ++tick);
        Assert.Equal(OwnMovementSim.ReconcileDecision.StopSnap, sim.LastReconcile.Decision);
        Assert.True(sim.LastReconcile.StopSettled);
    }

    [Fact]
    public void ImpliedLeadConvertsErrorToMilliseconds()
    {
        var sim = Sim();
        long tick = 4000;
        sim.FeedServerMotion(10f, 0f, 10f, 1, 0, 0);
        sim.Reconcile(0f, 0f, false, ++tick);
        for (var i = 0; i < 10; i++)
        {
            sim.Tick(50f);
            sim.FeedServerMotion(10f, 0f, 10f, 1, 0, 0);
            sim.Reconcile(0f, 0f, false, ++tick); // 服务端停在 0，本地一直走
        }
        var t = sim.LastReconcile;
        // err ≈ 本地领先量；impliedLeadMs = err / 预测速度 × 1000。
        Assert.True(t.ImpliedLeadMs > 0f);
        Assert.Equal(t.Err / t.LocalStepSpeed * 1000f, t.ImpliedLeadMs, 1);
    }
}
