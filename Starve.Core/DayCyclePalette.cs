using System.Numerics;

namespace Starve.Core;

/// <summary>
/// 服务端 DayCycle.Light 是一天里的时刻 0..1（不是亮度）：
/// 0 子夜 → 0.25 朝阳 → 0.5 盛阳 → 0.75 夕阳 → 1 子夜。
/// 季节叠加热烈/寒冷。雾不参与这套计算。
/// </summary>
public readonly record struct DayCycleLook(
    float TimeOfDay,
    float SunElevation,
    float MorningWeight,
    float NoonWeight,
    float DuskWeight,
    float NightWeight,
    float SunEnergy,
    float SunPitchDegrees,
    float SunYawDegrees,
    float AmbientEnergy,
    float FireEnergy,
    float LanternEnergy,
    Vector3 SunColor,
    Vector3 AmbientColor,
    Vector3 SkyTop,
    Vector3 SkyHorizon,
    Vector3 GroundHorizon);

public static class DayCyclePalette
{
    public const int SeasonSpring = 1;
    public const int SeasonSummer = 2;
    public const int SeasonAutumn = 3;
    public const int SeasonWinter = 4;

    private readonly record struct Key(
        float T,
        float Energy,
        float Pitch,
        float Ambient,
        float Fire,
        float Lantern,
        Vector3 Sun,
        Vector3 AmbientRgb,
        Vector3 SkyTop,
        Vector3 SkyHorizon,
        Vector3 Ground);

    // 子夜 → 朝阳 → 盛阳 → 夕阳 → 子夜。朝阳偏粉金，夕阳偏橙红，盛阳接近白。
    private static readonly Key[] Keys =
    [
        new(0.00f, 0.10f, 28f, 0.040f, 4.6f, 2.8f,
            new(0.38f, 0.50f, 0.92f), new(0.10f, 0.14f, 0.28f),
            new(0.03f, 0.05f, 0.12f), new(0.06f, 0.08f, 0.16f), new(0.04f, 0.05f, 0.08f)),
        new(0.22f, 0.82f, 12f, 0.18f, 2.8f, 1.4f,
            new(1.00f, 0.58f, 0.32f), new(0.62f, 0.32f, 0.22f),
            new(0.72f, 0.38f, 0.42f), new(1.00f, 0.62f, 0.38f), new(0.48f, 0.28f, 0.20f)),
        new(0.32f, 1.28f, 34f, 0.28f, 1.7f, 0.35f,
            new(1.00f, 0.84f, 0.62f), new(0.72f, 0.58f, 0.42f),
            new(0.42f, 0.58f, 0.88f), new(0.92f, 0.78f, 0.62f), new(0.52f, 0.46f, 0.34f)),
        new(0.50f, 1.72f, 58f, 0.38f, 1.25f, 0.08f,
            new(1.00f, 0.97f, 0.90f), new(0.78f, 0.82f, 0.88f),
            new(0.32f, 0.56f, 0.92f), new(0.78f, 0.82f, 0.88f), new(0.50f, 0.52f, 0.42f)),
        new(0.68f, 1.22f, 32f, 0.26f, 1.9f, 0.45f,
            new(1.00f, 0.76f, 0.48f), new(0.68f, 0.48f, 0.32f),
            new(0.48f, 0.42f, 0.72f), new(0.95f, 0.62f, 0.38f), new(0.46f, 0.32f, 0.22f)),
        new(0.78f, 0.70f, 11f, 0.16f, 3.4f, 1.6f,
            new(1.00f, 0.34f, 0.12f), new(0.55f, 0.18f, 0.10f),
            new(0.42f, 0.16f, 0.28f), new(0.95f, 0.38f, 0.16f), new(0.32f, 0.12f, 0.08f)),
        new(1.00f, 0.10f, 28f, 0.040f, 4.6f, 2.8f,
            new(0.38f, 0.50f, 0.92f), new(0.10f, 0.14f, 0.28f),
            new(0.03f, 0.05f, 0.12f), new(0.06f, 0.08f, 0.16f), new(0.04f, 0.05f, 0.08f)),
    ];

    public static DayCycleLook Evaluate(
        float timeOfDay,
        int season = 0,
        bool lightning = false,
        float rain = 0f)
    {
        var t = timeOfDay - MathF.Floor(timeOfDay);
        var key = Sample(t);
        var elev = MathF.Max(0f, -MathF.Cos(t * MathF.Tau));
        var morning = Bump(t, 0.25f, 0.12f);
        var noon = Bump(t, 0.50f, 0.16f);
        var dusk = Bump(t, 0.75f, 0.12f);
        var night = Bump(t, 0.00f, 0.18f);
        var wsum = morning + noon + dusk + night;
        if (wsum > 1e-5f)
        {
            morning /= wsum;
            noon /= wsum;
            dusk /= wsum;
            night /= wsum;
        }

        var energy = key.Energy;
        var pitch = key.Pitch;
        var ambient = key.Ambient;
        var sun = key.Sun;
        var ambRgb = key.AmbientRgb;
        ApplySeason(season, ref energy, ref pitch, ref ambient, ref sun, ref ambRgb);

        if (lightning)
        {
            energy += 1.15f;
            ambient += 0.35f;
            sun = Vector3.Lerp(sun, Vector3.One, 0.55f);
        }
        if (rain > 0.15f)
        {
            energy *= 0.86f;
            ambient *= 0.9f;
        }

        return new DayCycleLook(
            t,
            elev,
            morning,
            noon,
            dusk,
            night,
            energy,
            pitch,
            35f + 90f * MathF.Sin((t - 0.5f) * MathF.Tau),
            ambient,
            key.Fire,
            key.Lantern,
            sun,
            ambRgb,
            key.SkyTop,
            key.SkyHorizon,
            key.Ground);
    }

    private static Key Sample(float t)
    {
        for (var i = 0; i < Keys.Length - 1; i++)
        {
            if (t > Keys[i + 1].T) continue;
            var span = Keys[i + 1].T - Keys[i].T;
            var u = span < 1e-5f ? 0f : (t - Keys[i].T) / span;
            return Lerp(Keys[i], Keys[i + 1], Smooth(u));
        }
        return Keys[^1];
    }

    private static Key Lerp(Key a, Key b, float u) => new(
        0,
        Mix(a.Energy, b.Energy, u),
        Mix(a.Pitch, b.Pitch, u),
        Mix(a.Ambient, b.Ambient, u),
        Mix(a.Fire, b.Fire, u),
        Mix(a.Lantern, b.Lantern, u),
        Vector3.Lerp(a.Sun, b.Sun, u),
        Vector3.Lerp(a.AmbientRgb, b.AmbientRgb, u),
        Vector3.Lerp(a.SkyTop, b.SkyTop, u),
        Vector3.Lerp(a.SkyHorizon, b.SkyHorizon, u),
        Vector3.Lerp(a.Ground, b.Ground, u));

    private static void ApplySeason(
        int season,
        ref float energy,
        ref float pitch,
        ref float ambient,
        ref Vector3 sun,
        ref Vector3 ambRgb)
    {
        switch (season)
        {
            case SeasonSummer:
                energy *= 1.14f;
                pitch += 6f;
                ambient *= 1.18f;
                sun = Vector3.Lerp(sun, new Vector3(1f, 0.88f, 0.62f), 0.22f);
                ambRgb = Vector3.Lerp(ambRgb, new Vector3(0.95f, 0.62f, 0.32f), 0.2f);
                break;
            case SeasonWinter:
                energy *= 0.78f;
                pitch *= 0.78f;
                ambient *= 0.72f;
                sun = Vector3.Lerp(sun, new Vector3(0.72f, 0.82f, 1f), 0.35f);
                ambRgb = Vector3.Lerp(ambRgb, new Vector3(0.35f, 0.48f, 0.72f), 0.4f);
                break;
            case SeasonAutumn:
                energy *= 0.92f;
                sun = Vector3.Lerp(sun, new Vector3(1f, 0.62f, 0.32f), 0.18f);
                ambRgb = Vector3.Lerp(ambRgb, new Vector3(0.72f, 0.42f, 0.22f), 0.18f);
                break;
            default:
                // 春：略偏青绿，温和
                ambRgb = Vector3.Lerp(ambRgb, new Vector3(0.55f, 0.72f, 0.52f), 0.08f);
                break;
        }
    }

    private static float Bump(float t, float center, float width)
    {
        var d = MathF.Abs(t - center);
        if (d > 0.5f) d = 1f - d;
        return MathF.Max(0f, 1f - d / width);
    }

    private static float Mix(float a, float b, float u) => a + (b - a) * u;

    private static float Smooth(float u) => u * u * (3f - 2f * u);
}
