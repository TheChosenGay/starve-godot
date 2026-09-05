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

    [Fact]
    public void RotateViewYaw_KeepsLengthOfIsoW()
    {
        var (x, y) = MoveInput.RotateViewYaw(-1, -1, MathF.PI / 4f);
        Assert.Equal(MathF.Sqrt(2f), MathF.Sqrt(x * x + y * y), 4);
    }

    [Fact]
    public void WithViewYawSticky_HoldsPreviousNearBoundary()
    {
        var previous = MoveInput.WithViewYaw(-1, -1, 0f);
        // 转过一点但还没到 45°：滞回应保住原来的 8 向。
        var stuck = MoveInput.WithViewYawSticky(-1, -1, 0.2f, previous);
        Assert.Equal(previous, stuck);
        var flipped = MoveInput.WithViewYawSticky(-1, -1, MathF.PI / 2f, previous);
        Assert.Equal((-1, 1), flipped);
    }
}
