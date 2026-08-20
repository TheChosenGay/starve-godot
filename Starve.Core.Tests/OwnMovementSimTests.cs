using Starve.Core;

namespace Starve.Core.Tests;

public sealed class OwnMovementSimTests
{
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
}
