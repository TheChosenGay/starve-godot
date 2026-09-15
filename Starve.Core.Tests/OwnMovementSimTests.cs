using Starve.Core;

namespace Starve.Core.Tests;

public sealed class OwnMovementSimTests
{
    [Fact]
    public void UsesServerEffectiveSpeedForPrediction()
    {
        var sim = new OwnMovementSim((_, _) => true);
        sim.SnapTo(5f, 3f);
        sim.SetSpeed(5);
        sim.SetIntent(1, 0);

        sim.Tick(100);

        Assert.Equal(5.5f, sim.Position.X, 3);
    }

    [Fact]
    public void NegativeDirectionCrossesAnchorWithoutJumping()
    {
        var sim = new OwnMovementSim((_, _) => true);
        sim.SnapTo(5.1f, 3f);
        sim.SetSpeed(10);
        sim.SetIntent(-1, 0);

        sim.Tick(20);

        var position = sim.Position;
        Assert.Equal(4.9f, position.X, 3);
        Assert.Equal(3f, position.Y, 3);
    }

    [Fact]
    public void NegativeDirectionStopsAtBlockedBoundary()
    {
        var sim = new OwnMovementSim((x, y) => x >= 5 && y == 3);
        sim.SnapTo(5.1f, 3f);
        sim.SetSpeed(10);
        sim.SetIntent(-1, 0);

        sim.Tick(20);

        var position = sim.Position;
        Assert.Equal(5.001f, position.X, 3);
        Assert.Equal(3f, position.Y, 3);
    }

    [Fact]
    public void DiagonalMovementUsesNormalizedSpeed()
    {
        var sim = new OwnMovementSim((_, _) => true);
        sim.SnapTo(5f, 3f);
        sim.SetSpeed(10);
        sim.SetIntent(1, 1);

        sim.Tick(100);

        var position = sim.Position;
        Assert.Equal(5f + 1f / MathF.Sqrt(2f), position.X, 3);
        Assert.Equal(3f + 1f / MathF.Sqrt(2f), position.Y, 3);
    }

    [Fact]
    public void IsoProjectionRoundTripsWithHeight()
    {
        static float HeightAt(float x, float y) => 2.5f;
        var local = IsoMath.WorldToLocal(12.25f, 7.75f, HeightAt(0, 0));

        var world = IsoMath.LocalToWorld(local.X, local.Y, HeightAt);

        Assert.Equal(12.25f, world.X, 3);
        Assert.Equal(7.75f, world.Y, 3);
    }

    [Fact]
    public void ReconciliationExposesSoftAndHardCorrectionMetrics()
    {
        var sim = new OwnMovementSim((_, _) => true);
        sim.SnapTo(5f, 3f);

        sim.Reconcile(6f, 3f);
        sim.Reconcile(20f, 3f);

        var diagnostics = sim.Diagnostics;
        Assert.Equal(1, diagnostics.SoftCorrections);
        Assert.Equal(1, diagnostics.HardSnaps);
        Assert.True(diagnostics.MaxReconciliationError > 4f);
        Assert.True(diagnostics.LastReconciliationError > 4f);
    }

    [Fact]
    public void StoppedReconciliationKeepsErrorsInsideDeadZone()
    {
        var sim = new OwnMovementSim((_, _) => true);
        sim.SnapTo(5f, 3f);

        sim.Reconcile(5.1f, 3f, serverStopped: true);

        Assert.Equal(5f, sim.Position.X, 3);
        Assert.Equal(0, sim.Diagnostics.SoftCorrections);
        Assert.Equal(0.1f, sim.Diagnostics.LastReconciliationError, 3);
    }

    [Fact]
    public void StoppedReconciliationConvergesOutsideDeadZone()
    {
        var sim = new OwnMovementSim((_, _) => true);
        sim.SnapTo(5.4f, 3f);

        sim.Reconcile(5f, 3f, serverStopped: true);

        Assert.InRange(sim.Position.X, 5f, 5.4f);
        Assert.Equal(1, sim.Diagnostics.SoftCorrections);
    }

    [Fact]
    public void LargeReconciliationErrorSnapsExactly()
    {
        var sim = new OwnMovementSim((_, _) => true);
        sim.SnapTo(1f, 1f);

        sim.Reconcile(10f, 12f);

        Assert.Equal(10f, sim.Position.X, 3);
        Assert.Equal(12f, sim.Position.Y, 3);
        Assert.Equal(1, sim.Diagnostics.HardSnaps);
    }

    // 停下抖动回归锁：停下后服务端不再标脏 Position/Moveable，快照值会一直冻结在
    // 停下那一刻。客户端若每次都拿这份旧值收敛，就会"停下后被反复回拉"。
    // 同一 tick（或更旧）的停止快照只应生效一次。
    [Fact]
    public void StaleStoppedSnapshotIsAppliedOnlyOnce()
    {
        var sim = new OwnMovementSim((_, _) => true);
        sim.SnapTo(5.4f, 3f);

        // 第一个停止快照（tick=100）：正常收敛一次
        sim.Reconcile(5f, 3f, serverStopped: true, serverTick: 100);
        var afterFirst = sim.Position.X;
        Assert.Equal(1, sim.Diagnostics.SoftCorrections);
        Assert.InRange(afterFirst, 5f, 5.4f);

        // 同一份冻结快照又来 20 次：不得再产生任何移动/校正
        for (var i = 0; i < 20; i++)
        {
            sim.Reconcile(5f, 3f, serverStopped: true, serverTick: 100);
        }

        Assert.Equal(afterFirst, sim.Position.X, 6);
        Assert.Equal(1, sim.Diagnostics.SoftCorrections); // 仍然只有第一次
    }

    // 更旧的快照同样不能把角色往回拉。
    [Fact]
    public void OlderSnapshotIsIgnored()
    {
        var sim = new OwnMovementSim((_, _) => true);
        sim.SnapTo(5f, 3f);

        sim.Reconcile(6f, 3f, serverStopped: false, serverTick: 200);
        var after = sim.Position.X;

        // tick=150 的旧位置回来了：忽略
        sim.Reconcile(5f, 3f, serverStopped: false, serverTick: 150);

        Assert.Equal(after, sim.Position.X, 6);
    }

    // 停止落定应当一次到位，而不是分多次 20Hz 跳变逼近（渐进正是看得见的抖动）。
    [Fact]
    public void StoppedConvergenceSnapsInOneStep()
    {
        var sim = new OwnMovementSim((_, _) => true);
        sim.SnapTo(5.5f, 3f);

        sim.Reconcile(5f, 3f, serverStopped: true, serverTick: 100);

        Assert.Equal(5f, sim.Position.X, 3);
    }

    // 移动中的新鲜快照仍然照常校正（不能因为防抖把正常校正也关掉）。
    [Fact]
    public void FreshMovingSnapshotStillReconciles()
    {
        var sim = new OwnMovementSim((_, _) => true);
        sim.SnapTo(0f, 0f);

        sim.Reconcile(1f, 0f, serverStopped: false, serverTick: 100);

        Assert.Equal(1, sim.Diagnostics.SoftCorrections);
        Assert.True(sim.Position.X > 0f);
    }
}
