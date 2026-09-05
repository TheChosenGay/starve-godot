using Starve.Core;

namespace Starve.Core.Tests;

public sealed class DayCyclePaletteTests
{
    [Fact]
    public void Noon_IsBrightWhite()
    {
        var look = DayCyclePalette.Evaluate(0.5f);
        Assert.True(look.SunEnergy > 1.4f);
        Assert.True(look.SunPitchDegrees > 45f);
        Assert.True(look.NoonWeight > look.MorningWeight);
        Assert.True(look.SunColor.Y > 0.85f);
    }

    [Fact]
    public void Midnight_IsCoolAndDim()
    {
        var look = DayCyclePalette.Evaluate(0f);
        Assert.True(look.SunEnergy < 0.25f);
        Assert.True(look.NightWeight > 0.5f);
        Assert.True(look.SunColor.Z > look.SunColor.X);
        Assert.True(look.FireEnergy > 3.5f);
        Assert.True(look.LanternEnergy > 2f);
    }

    [Fact]
    public void Dawn_IsPeach_Dusk_IsDeeperRed()
    {
        var dawn = DayCyclePalette.Evaluate(0.25f);
        var dusk = DayCyclePalette.Evaluate(0.75f);
        Assert.True(dawn.MorningWeight > dusk.MorningWeight);
        Assert.True(dusk.DuskWeight > dawn.DuskWeight);
        Assert.True(dusk.SunColor.Y < dawn.SunColor.Y);
        Assert.True(dawn.SunPitchDegrees < 25f);
        Assert.True(dusk.SunPitchDegrees < 25f);
    }

    [Fact]
    public void SunTravelsEastToWest()
    {
        var dawn = DayCyclePalette.Evaluate(0.25f);
        var noon = DayCyclePalette.Evaluate(0.5f);
        var dusk = DayCyclePalette.Evaluate(0.75f);
        Assert.True(dawn.SunYawDegrees < noon.SunYawDegrees);
        Assert.True(noon.SunYawDegrees < dusk.SunYawDegrees);
    }

    [Fact]
    public void SummerIsHotterThanWinter()
    {
        var summer = DayCyclePalette.Evaluate(0.5f, DayCyclePalette.SeasonSummer);
        var winter = DayCyclePalette.Evaluate(0.5f, DayCyclePalette.SeasonWinter);
        Assert.True(summer.SunEnergy > winter.SunEnergy);
        Assert.True(summer.AmbientEnergy > winter.AmbientEnergy);
        Assert.True(winter.SunColor.Z > summer.SunColor.Z);
        Assert.True(summer.SunColor.Y > winter.SunColor.Y);
    }

    [Fact]
    public void LightningRaisesEnergy()
    {
        var calm = DayCyclePalette.Evaluate(0.2f);
        var bolt = DayCyclePalette.Evaluate(0.2f, lightning: true);
        Assert.True(bolt.SunEnergy > calm.SunEnergy + 0.5f);
        Assert.True(bolt.AmbientEnergy > calm.AmbientEnergy);
    }

    [Fact]
    public void RainDimsSun()
    {
        var dry = DayCyclePalette.Evaluate(0.5f);
        var wet = DayCyclePalette.Evaluate(0.5f, rain: 0.4f);
        Assert.True(wet.SunEnergy < dry.SunEnergy);
    }
}
