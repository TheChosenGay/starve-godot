using GodotClient.Game;
using Starve.Game.V1;

namespace Starve.Core.Tests;

public sealed class PlayerPresentationStateTests
{
    [Fact]
    public void DamageFlashAcceptsOnlyAuthoritativeHitOnLocalPlayer()
    {
        var flash = new DamageFlashState();

        Assert.False(flash.ApplyImpact(CombatImpactResult.Miss, true));
        Assert.False(flash.ApplyImpact(CombatImpactResult.Blocked, true));
        Assert.False(flash.ApplyImpact(CombatImpactResult.Immune, true));
        Assert.False(flash.ApplyImpact(CombatImpactResult.Hit, false));
        Assert.Equal(0, flash.Alpha);

        Assert.True(flash.ApplyImpact(CombatImpactResult.Hit, true));
        Assert.Equal(DamageFlashState.HitIntensity, flash.Alpha, 3);
    }

    [Fact]
    public void DamageFlashFadesAt350MsAndConsecutiveHitsCapIntensity()
    {
        var flash = new DamageFlashState();
        flash.ApplyImpact(CombatImpactResult.Hit, true);
        flash.ApplyImpact(CombatImpactResult.Hit, true);
        flash.ApplyImpact(CombatImpactResult.Hit, true);

        Assert.Equal(DamageFlashState.MaxIntensity, flash.Alpha, 3);
        flash.Advance(175);
        Assert.InRange(flash.Alpha, 0.38f, 0.4f);
        flash.ApplyImpact(CombatImpactResult.Hit, true);
        Assert.InRange(flash.Alpha, 0.75f, DamageFlashState.MaxIntensity);
        flash.Advance(350);
        Assert.Equal(0, flash.Alpha);
    }

    [Fact]
    public void SpiritStateSwitchesWithoutWorldPositionAccumulation()
    {
        var spirit = new SpiritPresentationState();
        Assert.False(spirit.IsDead);
        Assert.True(spirit.SetDead(true));
        Assert.False(spirit.SetDead(true));

        var first = spirit.Advance(375);
        var second = spirit.Advance(375);
        Assert.InRange(first.BobOffset, 2.9f, 3.1f);
        Assert.InRange(second.BobOffset, -0.01f, 0.01f);
        Assert.InRange(first.Alpha, 0.58f, 0.66f);

        Assert.True(spirit.SetDead(false));
        Assert.Equal(new SpiritVisualSample(0, 1, 1), spirit.Advance(1000));
    }

    [Fact]
    public void HudVitalsSignatureIncludesHealthAndDeadState()
    {
        var healthy = HudVitalsViewModel.Create(80, 100, false);
        var hurt = HudVitalsViewModel.Create(20, 100, false);
        var dead = HudVitalsViewModel.Create(20, 100, true);

        Assert.NotEqual(healthy.Signature, hurt.Signature);
        Assert.NotEqual(hurt.Signature, dead.Signature);
        Assert.Equal("生命 80 / 100", healthy.Text);
        Assert.Equal("灵魂状态 · 生命 0 / 100", dead.Text);
        Assert.Equal(VitalTone.Red, hurt.Tone);
        Assert.Equal(VitalTone.Spirit, dead.Tone);
    }
}
