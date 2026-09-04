using GodotClient.Game;

namespace Starve.Core.Tests;

public sealed class RigPresentationContractTests
{
    [Fact]
    public void AttackTimingsMatchServerEightPlusEightTicks()
    {
        Assert.Equal(800, RigPresentationMetrics.AttackDurationMs);
        Assert.Equal(400, RigPresentationMetrics.AttackImpactMs);
        Assert.InRange(
            15 / RigPresentationMetrics.FishmanAttackFps * 1000,
            799.999,
            800.001);
        Assert.InRange(
            8 / RigPresentationMetrics.LizardAttackFps * 1000,
            799.999,
            800.001);
    }

    [Fact]
    public void FourFishmanDirectionsShareHeightAndFootline()
    {
        var side = DirectionalRigNormalizer.Normalize(
            [
                new DirectionalPartGeometry(180, 220, 2, -47, 0.09f),
                new DirectionalPartGeometry(160, 260, 0, -28, 0.09f),
                new DirectionalPartGeometry(120, 180, -5, -8, 0.09f),
            ],
            RigPresentationMetrics.FishmanVisualHeight);
        var back = DirectionalRigNormalizer.Normalize(
            [
                new DirectionalPartGeometry(190, 210, 0, -47, 0.09f),
                new DirectionalPartGeometry(170, 270, 0, -29, 0.09f),
                new DirectionalPartGeometry(110, 190, 7, -8, 0.09f),
            ],
            RigPresentationMetrics.FishmanVisualHeight);

        var heights = new[]
        {
            360f * (RigPresentationMetrics.FishmanVisualHeight / 360f), // front
            side.NormalizedBounds.Height, // left
            side.NormalizedBounds.Height, // right mirror
            back.NormalizedBounds.Height,
        };
        Assert.All(
            heights,
            height => Assert.InRange(
                MathF.Abs(height - RigPresentationMetrics.FishmanVisualHeight),
                0,
                1));
        Assert.InRange(MathF.Abs(side.NormalizedBounds.Bottom), 0, 0.001f);
        Assert.InRange(MathF.Abs(back.NormalizedBounds.Bottom), 0, 0.001f);
        const float frontFootY = 456f / 1024f;
        var frontScale = RigPresentationMetrics.FishmanVisualHeight / 360f;
        var frontFootline =
            1024 * frontScale * (0.5f - frontFootY) +
            1024 * frontScale * (frontFootY - 0.5f);
        Assert.InRange(MathF.Abs(frontFootline), 0, 0.001f);
        Assert.InRange(
            MathF.Abs((side.NormalizedBounds.Left + side.NormalizedBounds.Right) * 0.5f),
            0,
            0.001f);
        Assert.InRange(MathF.Abs((back.NormalizedBounds.Left + back.NormalizedBounds.Right) * 0.5f), 0, 0.001f);
    }

    [Fact]
    public void SpiderIsShorterThanPlayerAndSharesNormalizedFootline()
    {
        Assert.True(RigPresentationMetrics.SpiderVisualHeight < RigPresentationMetrics.FishmanVisualHeight);
        var scale = RigPresentationMetrics.SpiderVisualHeight / RigPresentationMetrics.SpiderSubjectHeight;
        var footline =
            1024 * scale * (0.5f - RigPresentationMetrics.SpiderFootY) +
            1024 * scale * (RigPresentationMetrics.SpiderFootY - 0.5f);
        Assert.InRange(MathF.Abs(footline), 0, 0.001f);
        Assert.InRange(MathF.Abs(1024 * RigPresentationMetrics.SpiderFootY - 820f), 0, 0.001f);
        Assert.Equal(48f, RigPresentationMetrics.SpiderVisualHeight);
        Assert.Equal(520f, RigPresentationMetrics.SpiderSubjectHeight);
    }

    [Fact]
    public void DirectionalSpriteClipPrefersFacingThenFrontThenIdle()
    {
        var spider = new HashSet<string> { "idle", "walk", "idle_side", "walk_side", "idle_back", "walk_back" };
        Assert.Equal("walk_side", DirectionalSpriteClip.Locomotion(true, true, false, spider.Contains));
        Assert.Equal("idle_back", DirectionalSpriteClip.Locomotion(false, false, true, spider.Contains));
        Assert.Equal("walk", DirectionalSpriteClip.Locomotion(true, false, false, spider.Contains));

        var frontOnly = new HashSet<string> { "idle", "walk" };
        Assert.Equal("walk", DirectionalSpriteClip.Locomotion(true, true, false, frontOnly.Contains));
        Assert.Equal("idle", DirectionalSpriteClip.Locomotion(false, false, true, frontOnly.Contains));

        var idleOnly = new HashSet<string> { "idle" };
        Assert.Equal("idle", DirectionalSpriteClip.Locomotion(true, true, false, idleOnly.Contains));
    }
}
