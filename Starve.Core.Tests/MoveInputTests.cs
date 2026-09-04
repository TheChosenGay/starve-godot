using Starve.Core;

namespace Starve.Core.Tests;

public sealed class MoveInputTests
{
    [Fact]
    public void WithViewYaw_ZeroKeepsIsoW()
    {
        Assert.Equal((-1, -1), MoveInput.WithViewYaw(-1, -1, 0f));
    }

    [Fact]
    public void WithViewYaw_QuarterTurnMapsWToScreenUp()
    {
        var got = MoveInput.WithViewYaw(-1, -1, MathF.PI / 2f);
        Assert.Equal((-1, 1), got);
    }

    [Fact]
    public void WithViewYaw_HalfTurnReversesW()
    {
        var got = MoveInput.WithViewYaw(-1, -1, MathF.PI);
        Assert.Equal((1, 1), got);
    }
}
