using GodotClient.Game;
using Starve.Game.V1;

namespace Starve.Core.Tests;

public sealed class HauntInteractionPolicyTests
{
    [Fact]
    public void PredictedAndAuthoritativeHauntLockGameplay()
    {
        var predicted = Status(predicted: true, uninterruptible: false);
        var authoritative = Status(predicted: false, uninterruptible: true);

        Assert.True(HauntInteractionPolicy.IsGameplayLocked(predicted));
        Assert.True(HauntInteractionPolicy.IsGameplayLocked(authoritative));
        Assert.False(HauntInteractionPolicy.IsGameplayLocked(null));
    }

    [Theory]
    [InlineData(false, true, 1, 0, 0, 1, 0, 1, 1, HauntValidation.ActorAlive)]
    [InlineData(true, false, 1, 0, 0, 1, 0, 1, 1, HauntValidation.TargetMissing)]
    [InlineData(true, true, 0, 0, 0, 1, 0, 1, 1, HauntValidation.Depleted)]
    [InlineData(true, true, 1, 0, 0, 3, 0, 1, 1, HauntValidation.OutOfRange)]
    [InlineData(true, true, 1, 0, 0, 2, 0, 1, 1, HauntValidation.Allowed)]
    [InlineData(true, true, 1, 3, 0, 0, 0, 2, 1, HauntValidation.Allowed)]
    public void ValidationCoversLifeTargetUsesRangeAndFootprint(
        bool dead,
        bool hauntable,
        int uses,
        int actorX,
        int actorY,
        int targetX,
        int targetY,
        int width,
        int height,
        HauntValidation expected)
    {
        Assert.Equal(expected, HauntInteractionPolicy.Validate(
            dead,
            hauntable,
            uses,
            actorX,
            actorY,
            targetX,
            targetY,
            width,
            height));
    }

    private static ActionPresentationStatus Status(bool predicted, bool uninterruptible) =>
        new(predicted, ActionKind.Haunt, 1, ActionPhase.Windup, 2, 3, 4, uninterruptible);
}
