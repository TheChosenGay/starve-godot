using Starve.Core;

namespace Starve.Core.Tests;

public sealed class BlockVisualTests
{
    [Fact]
    public void OneByOneCentersOnTileMidpoint()
    {
        var (x, y) = BlockVisual.Center(10, 7, 1, 1);
        Assert.Equal(10.5f, x);
        Assert.Equal(7.5f, y);
    }

    [Fact]
    public void TwoByTwoCentersOnFootprintMidpoint()
    {
        var (x, y) = BlockVisual.Center(4, 4, 2, 2);
        Assert.Equal(5f, x);
        Assert.Equal(5f, y);
    }

    [Fact]
    public void NonPositiveSizeFallsBackToOneTile()
    {
        var (x, y) = BlockVisual.Center(1, 2, 0, -3);
        Assert.Equal(1.5f, x);
        Assert.Equal(2.5f, y);
    }
}
