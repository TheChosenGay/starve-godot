using Starve.Core;
using Starve.Netcode;

namespace Starve.Core.Tests;

/// <summary>
/// <see cref="ComponentOwnMovementSim"/> 的接线测试（**不启动 Godot**）：
/// 它就是 <c>IOwnMovementSim</c> 的组件实现，GameRoot 的既有调用面一行不用改。
///
/// 要钉住的接线语义：
/// <list type="number">
/// <item>意图 → 预测：SetIntent 之后按 tick 推进，位置按速度前进（和服务端同公式）；</item>
/// <item>快照 → 和解：把"**我自己执行完第 N 条之后的状态**"当权威回灌 ⇒ 误差必须为 0（无假失配）；</item>
/// <item>上行：未确认窗口可取出（交给 CommandService.SendMoveOps）；</item>
/// <item>出生/传送：SnapTo 之后链贴到新位置，且旧的序号状态作废。</item>
/// </list>
/// </summary>
public sealed class ComponentOwnMovementSimTests
{
    private const long Tick0 = 1000;
    private const long Now0 = 50_000;            // Tick0 × 50ms
    private const ulong Epoch = 7;

    private static ComponentOwnMovementSim New()
    {
        var sim = new ComponentOwnMovementSim((_, _) => true);
        sim.HeightAt = (_, _) => 0f;
        sim.SetSpeed(10f);
        return sim;
    }

    [Fact]
    public void AdapterDrivesPredictionAndReconcilesBySequence()
    {
        var sim = New();
        Assert.False(sim.Has);

        // 首份快照建链（tick 1000，now 对齐）
        sim.Reconcile(0f, 0f, false, Tick0, appliedSeq: 0, epoch: Epoch, nowMs: Now0);
        Assert.True(sim.Has);

        sim.SetIntent(1, 0);
        for (var i = 1; i <= 5; i++) sim.Tick(50f, Now0 + i * 50L);

        // 模拟推进了 5 tick × 0.5 格（SimPosition = 权威正确的模拟状态）
        Assert.Equal(2.5f, sim.SimPosition.X, 2);
        // 渲染位置是"上一条 tick → 最新 tick"之间插值（默认延迟 1 tick），此刻正好落在上一条上
        Assert.Equal(2.0f, sim.Position.X, 2);
        Assert.Equal((1, 0), sim.Intent);
        Assert.True(sim.Moving);

        // ★ 同序号比较：把"我自己执行完第 3 条之后的状态"当权威回灌 ⇒ 误差必须为 0
        Assert.True(sim.Smoother.TryGetOpState(3, out var mine));
        sim.Reconcile(mine.X, mine.Y, false, Tick0 + 3, appliedSeq: 3, epoch: Epoch, nowMs: Now0 + 5 * 50L);
        Assert.Equal(0f, sim.Diagnostics.LastReconciliationError, 3);
        Assert.Equal(OwnMovementSim.ReconcileDecision.NoError, sim.LastReconcile.Decision);

        // 收尾：模拟状态没被这次"和解"改动（同序号 ⇒ 零误差 ⇒ 不 rebase）
        Assert.Equal(2.5f, sim.SimPosition.X, 2);
    }

    [Fact]
    public void AdapterExposesUnackedWindowForUplink()
    {
        var sim = New();
        sim.Reconcile(0f, 0f, false, Tick0, appliedSeq: 0, epoch: Epoch, nowMs: Now0);
        sim.SetIntent(1, 0);
        for (var i = 1; i <= 5; i++) sim.Tick(50f, Now0 + i * 50L);

        var ops = new List<ClientSmoother<OwnMoveState, MoveIntent>.OpRef>();
        Assert.Equal(3, sim.CollectUnackedOps(ops));    // 窗口上限 3
        Assert.Equal(5UL, ops[^1].Seq);                 // 最新一条
        Assert.Equal(1, ops[^1].Action.Dx);

        // 服务端确认到 5 之后，窗口清空
        sim.Reconcile(sim.Position.X, sim.Position.Y, false, Tick0 + 5, appliedSeq: 5, epoch: Epoch, nowMs: Now0 + 5 * 50L);
        Assert.Equal(0, sim.CollectUnackedOps(ops));
    }

    [Fact]
    public void SnapToTeleportsAndInvalidatesOldSequenceStates()
    {
        var sim = New();
        sim.Reconcile(0f, 0f, false, Tick0, appliedSeq: 0, epoch: Epoch, nowMs: Now0);
        sim.SetIntent(1, 0);
        for (var i = 1; i <= 3; i++) sim.Tick(50f, Now0 + i * 50L);
        Assert.True(sim.Smoother.OpHistoryCount > 0);

        sim.SnapTo(100f, 20f);                          // 出生/传送
        Assert.Equal(100f, sim.Position.X, 3);
        Assert.Equal(20f, sim.Position.Y, 3);
        Assert.Equal(0, sim.Smoother.OpHistoryCount);   // 旧位置对应的序号状态作废（不能被拿来比）
    }
}
