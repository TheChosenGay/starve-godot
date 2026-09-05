using Godot;
using Starve.Core;

namespace GodotClient.Game;

/// <summary>
/// 吉卜力风体积云天空：光线步进 + FBM。
/// 云偏蓬松、亮、边缘软；颜色跟日夜周期走。
/// </summary>
public static class GhibliSky
{
    public static ShaderMaterial Create()
    {
        var mat = new ShaderMaterial { Shader = ShaderLibrary.Load(ShaderLibrary.CloudSky) };
        ApplyDefaults(mat);
        return mat;
    }

    public static ShaderMaterial CreateVolume()
    {
        var mat = new ShaderMaterial { Shader = ShaderLibrary.Load(ShaderLibrary.CloudVolume) };
        ApplyDefaults(mat);
        return mat;
    }

    public static ShaderMaterial CreateShadow()
    {
        var mat = new ShaderMaterial { Shader = ShaderLibrary.Load(ShaderLibrary.CloudShadow) };
        ApplyDefaults(mat);
        return mat;
    }

    public static Sky CreateSky(ShaderMaterial mat) => new()
    {
        SkyMaterial = mat,
        ProcessMode = Sky.ProcessModeEnum.Realtime,
        RadianceSize = Sky.RadianceSizeEnum.Size256,
    };

    public static void Apply(
        ShaderMaterial mat,
        DayCycleLook look,
        LightTune tune,
        Vector3 towardSun,
        float rain,
        Vector3 shadowRay = default)
    {
        if (towardSun.LengthSquared() < 1e-6f)
            towardSun = new Vector3(0.32f, 0.72f, -0.58f);
        else
            towardSun = towardSun.Normalized();
        if (shadowRay.LengthSquared() < 1e-6f)
            shadowRay = new Vector3(-0.5f, -0.707f, -0.5f);
        else
            shadowRay = shadowRay.Normalized();

        var cover = Mathf.Clamp(tune.CloudCoverage + rain * 0.26f, 0.04f, 0.95f);
        mat.SetShaderParameter("coverage", cover);
        mat.SetShaderParameter("thickness", tune.CloudThickness);
        mat.SetShaderParameter("wind", tune.CloudWind);
        mat.SetShaderParameter("absorption", 0.9f);
        mat.SetShaderParameter("sun_direction", towardSun);
        mat.SetShaderParameter("shadow_ray", shadowRay);
        mat.SetShaderParameter("sky_top", ToColor(look.SkyTop));
        mat.SetShaderParameter("sky_horizon", ToColor(look.SkyHorizon));
        mat.SetShaderParameter("ground_horizon", ToColor(look.GroundHorizon));
        mat.SetShaderParameter("ground_bottom", ToColor(look.GroundHorizon * 0.45f));
        mat.SetShaderParameter("sun_color", ToColor(look.SunColor));
        mat.SetShaderParameter("sun_energy", look.SunEnergy);
        mat.SetShaderParameter("ambient_color", ToColor(look.AmbientColor));
        mat.SetShaderParameter("night", look.NightWeight);
        mat.SetShaderParameter("cloud_lit", CloudLit(look));
        mat.SetShaderParameter("cloud_shadow", CloudShadow(look));
    }

    public static void ApplyDefaults(ShaderMaterial mat) =>
        Apply(mat, DayCyclePalette.Evaluate(0.5f), LightTune.Default, new Vector3(0.32f, 0.72f, -0.58f), 0f);

    private static Color CloudLit(DayCycleLook look)
    {
        var sun = ToColor(look.SunColor);
        var amb = ToColor(look.AmbientColor);
        var noon = sun.Lerp(new Color(0.96f, 0.97f, 0.98f), 0.22f * look.NoonWeight);
        var dusk = sun.Lerp(amb, 0.12f);
        var day = noon.Lerp(dusk, Mathf.Clamp(look.DuskWeight + look.MorningWeight * 0.55f, 0f, 1f));
        var night = new Color(amb.R * 0.45f, amb.G * 0.55f, amb.B * 0.85f);
        return day.Lerp(night, look.NightWeight);
    }

    private static Color CloudShadow(DayCycleLook look)
    {
        var sun = ToColor(look.SunColor);
        var amb = ToColor(look.AmbientColor);
        var day = amb.Lerp(sun, 0.18f) * new Color(0.52f, 0.58f, 0.66f);
        var night = new Color(amb.R * 0.18f, amb.G * 0.24f, amb.B * 0.42f);
        return day.Lerp(night, look.NightWeight);
    }

    private static Color ToColor(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
}
