using System;
using System.Collections.Generic;
using Godot;

namespace GodotClient.Game;

/// <summary>
/// 3D LUT 调色图集：白天/黄昏/夜晚/青橙电影/胶片/阴天 6 套预设烘焙成 1024×192
/// 大贴图（每套 32³），shader 按权重混合。预设与 web 端 lut-filter.ts 一致。
/// </summary>
public sealed class LutBuilder
{
    public const int N = 32;
    public const int Styles = 6;

    private sealed record Preset(
        float Brightness,
        float Contrast,
        float Saturation,
        float Temperature,
        float Shadows,
        float Highlights,
        float Shoulder,
        (float, float, float)? ShadowTint,
        (float, float, float)? HighlightTint);

    private static readonly Preset[] Presets =
    {
        new(1.02f, 1.08f, 1.12f, 0.05f, -0.02f, 0f, 0.12f, null, null), // day
        new(0.95f, 1.05f, 1.05f, 0.30f, 0.06f, 0.03f, 0.20f, null, null), // dusk
        new(0.92f, 1.10f, 0.82f, -0.18f, -0.02f, 0f, 0.15f, null, null), // night
        new(1.00f, 1.18f, 1.16f, 0.02f, -0.02f, 0.02f, 0.22f, (-0.14f, 0.03f, 0.10f), (0.08f, 0f, -0.05f)), // teal
        new(1.00f, 0.98f, 0.80f, 0.07f, 0.10f, -0.03f, 0.32f, (0f, 0.02f, 0.01f), null), // film
        new(0.94f, 0.94f, 0.70f, -0.08f, 0.04f, -0.04f, 0.25f, (-0.02f, 0f, 0.04f), null), // overcast
    };

    public ImageTexture Atlas { get; private set; } = null!;

    public static LutBuilder Build()
    {
        var w = N * N;
        var img = Image.CreateEmpty(w, Styles * N, false, Image.Format.Rgba8);
        for (var row = 0; row < Styles; row++)
        {
            BakeStrip(img, Presets[row], row);
        }
        return new LutBuilder { Atlas = ImageTexture.CreateFromImage(img) };
    }

    private static void BakeStrip(Image img, Preset p, int row)
    {
        var w = N * N;
        for (var py = 0; py < N; py++)
        {
            for (var px = 0; px < w; px++)
            {
                var r = (px % N) / (float)(N - 1);
                var g = py / (float)(N - 1);
                var b = (px / N) / (float)(N - 1);
                var (or, og, ob) = StyleColor(r, g, b, p);
                img.SetPixel(px, row * N + py, new Color(Clamp01(or), Clamp01(og), Clamp01(ob)));
            }
        }
    }

    private static (float, float, float) StyleColor(float r, float g, float b, Preset p)
    {
        var lum = r * 0.299f + g * 0.587f + b * 0.114f;
        r *= p.Brightness;
        g *= p.Brightness;
        b *= p.Brightness;
        r = (r - 0.5f) * p.Contrast + 0.5f;
        g = (g - 0.5f) * p.Contrast + 0.5f;
        b = (b - 0.5f) * p.Contrast + 0.5f;

        var sh = Mathf.Max(0, lum - 0.75f);
        var knee = p.Shoulder * sh * sh;
        r -= knee;
        g -= knee;
        b -= knee;

        lum = r * 0.299f + g * 0.587f + b * 0.114f;
        r = lum + (r - lum) * p.Saturation;
        g = lum + (g - lum) * p.Saturation;
        b = lum + (b - lum) * p.Saturation;

        var warm = 1 - lum * 0.6f;
        r += p.Temperature * warm * 0.35f;
        b -= p.Temperature * warm * 0.35f;

        var lum2 = r * 0.299f + g * 0.587f + b * 0.114f;
        var sw = Mathf.Max(0, 1 - lum2 / 0.5f);
        var hw = Mathf.Max(0, lum2 / 0.5f - 1);
        r += p.Shadows * sw * 0.35f + p.Highlights * hw * 0.35f;
        g += p.Shadows * sw * 0.35f + p.Highlights * hw * 0.35f;
        b += p.Shadows * sw * 0.35f + p.Highlights * hw * 0.35f;
        if (p.ShadowTint is { } st)
        {
            r += st.Item1 * sw * 0.5f;
            g += st.Item2 * sw * 0.5f;
            b += st.Item3 * sw * 0.5f;
        }
        if (p.HighlightTint is { } ht)
        {
            r += ht.Item1 * hw * 0.5f;
            g += ht.Item2 * hw * 0.5f;
            b += ht.Item3 * hw * 0.5f;
        }
        return (r, g, b);
    }

    private static float Clamp01(float v) => Mathf.Clamp(v, 0, 1);
}
