using GodotClient.Game;
using Starve.Core;

namespace Starve.Core.Tests;

public sealed class MovementDiagnosticsSamplerTests
{
    [Fact]
    public void SamplesAtConfiguredInterval()
    {
        var reads = 0;
        var sampler = new MovementDiagnosticsSampler(
            () =>
            {
                reads++;
                return new MovementDiagnostics(reads, reads, reads, reads);
            },
            intervalMs: 1000);

        Assert.True(sampler.TrySample(100, out var first, out var firstChanged));
        Assert.False(sampler.TrySample(1099, out var cached, out var cachedChanged));
        Assert.True(sampler.TrySample(1100, out var second, out var secondChanged));

        Assert.True(firstChanged);
        Assert.False(cachedChanged);
        Assert.True(secondChanged);
        Assert.Equal(first, cached);
        Assert.Equal(2, reads);
        Assert.Equal(2, second.SoftCorrections);
    }

    [Fact]
    public void UnchangedSnapshotDoesNotRequestAnotherLog()
    {
        var diagnostics = new MovementDiagnostics(0.1f, 0.2f, 1, 0);
        var sampler = new MovementDiagnosticsSampler(() => diagnostics, intervalMs: 500);

        Assert.True(sampler.TrySample(0, out _, out var firstChanged));
        Assert.True(sampler.TrySample(500, out _, out var secondChanged));

        Assert.True(firstChanged);
        Assert.False(secondChanged);
    }

    [Fact]
    public void SamplesImmediatelyAfterClockMovesBackward()
    {
        var reads = 0;
        var sampler = new MovementDiagnosticsSampler(
            () => new MovementDiagnostics(++reads, reads, reads, reads),
            intervalMs: 1000);

        Assert.True(sampler.TrySample(10_000, out _, out _));
        Assert.True(sampler.TrySample(100, out var afterRollback, out var changed));

        Assert.True(changed);
        Assert.Equal(2, reads);
        Assert.Equal(2, afterRollback.SoftCorrections);
    }
}
