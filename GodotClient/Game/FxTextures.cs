using Godot;

namespace GodotClient.Game;

/// <summary>程序化特效纹理缓存：火焰 / 柔光晕 / 光柱 / 粒子圆点。
/// 供 FireView（世界空间火焰）与 VolumetricView（LUT 后加法体积光）共用。</summary>
public static class FxTextures
{
    private static Texture2D? _flame;
    private static Texture2D? _glow;
    private static Texture2D? _shaft;
    private static Texture2D? _dot;

    public static Texture2D Flame => _flame ??= BuildFlameTexture();
    public static Texture2D Glow => _glow ??= BuildGlowTexture();
    public static Texture2D Shaft => _shaft ??= BuildShaftTexture();
    public static Texture2D Dot => _dot ??= BuildDotTexture();

    /// <summary>火焰贴图：底部亮黄、顶尖变细变红（带轻微扭曲），64×96。</summary>
    private static Texture2D BuildFlameTexture()
    {
        const int w = 64, h = 96;
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        for (var y = 0; y < h; y++)
        {
            var ny = y / (float)(h - 1);
            var dy = 1f - ny; // 1 底部 → 0 顶部
            var centerX = 0.5f + Mathf.Sin(ny * 13f) * 0.045f;
            var halfW = 0.13f + 0.30f * dy;
            for (var x = 0; x < w; x++)
            {
                var dx = (x / (float)(w - 1) - centerX) / halfW;
                var fall = Mathf.Max(0f, 1f - dx * dx);
                if (fall <= 0.001f)
                {
                    img.SetPixel(x, y, new Color(0, 0, 0, 0));
                    continue;
                }
                var topFade = Mathf.SmoothStep(0f, 0.22f, dy);
                var alpha = fall * topFade;
                img.SetPixel(x, y, new Color(
                    Mathf.Lerp(1f, 0.92f, dy),
                    Mathf.Lerp(0.9f, 0.25f, dy),
                    Mathf.Lerp(0.55f, 0.05f, dy),
                    alpha));
            }
        }
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>柔光晕贴图：二次方径向衰减（比线性更柔和，光模糊的核心）。</summary>
    private static Texture2D BuildGlowTexture()
    {
        const int s = 128;
        var img = Image.CreateEmpty(s, s, false, Image.Format.Rgba8);
        for (var y = 0; y < s; y++)
        {
            for (var x = 0; x < s; x++)
            {
                var d = new Vector2(x - s / 2, y - s / 2).Length() / (s / 2f);
                var a = Mathf.Max(0f, 1f - d);
                a = a * a;
                img.SetPixel(x, y, new Color(1, 1, 1, a));
            }
        }
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>光柱贴图：底部宽、顶部收窄，边缘软衰减，96×192。</summary>
    private static Texture2D BuildShaftTexture()
    {
        const int w = 96, h = 192;
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        for (var y = 0; y < h; y++)
        {
            var ny = y / (float)(h - 1);
            var dy = 1f - ny; // 1 底部 → 0 顶部
            var halfW = 0.14f + 0.28f * dy;
            var fade = dy * dy * Mathf.SmoothStep(0.06f, 0.28f, dy); // 底部最强、往上渐隐
            for (var x = 0; x < w; x++)
            {
                var dx = (x / (float)(w - 1) - 0.5f) / halfW;
                var fall = Mathf.Max(0f, 1f - dx * dx);
                img.SetPixel(x, y, new Color(1, 1, 1, fall * fade));
            }
        }
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>软圆点（余烬粒子），16×16。</summary>
    private static Texture2D BuildDotTexture()
    {
        const int s = 16;
        var img = Image.CreateEmpty(s, s, false, Image.Format.Rgba8);
        for (var y = 0; y < s; y++)
        {
            for (var x = 0; x < s; x++)
            {
                var d = new Vector2(x - s / 2, y - s / 2).Length() / (s / 2f);
                img.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp(1f - d, 0f, 1f)));
            }
        }
        return ImageTexture.CreateFromImage(img);
    }
}
