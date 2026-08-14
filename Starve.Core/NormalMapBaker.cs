using System;

namespace Starve.Core;

/// <summary>
/// 全图法线图：每格一个纹素，RGB 编码该格地面法线（高度场梯度 → 归一化）。
/// 水面格法线取平；slopeScale 让坡面不至于过陡。纯逻辑，输出 RGBA 缓冲。
/// </summary>
public static class NormalMapBaker
{
    public const float SlopeScale = 0.6f;

    public static void Bake(TileMap tm, byte[] rgba)
    {
        var w = tm.Width;
        var h = tm.Height;
        for (var cy = 0; cy < h; cy++)
        {
            for (var cx = 0; cx < w; cx++)
            {
                var h00 = tm.CornerHeight(cx, cy);
                var h10 = tm.CornerHeight(cx + 1, cy);
                var h01 = tm.CornerHeight(cx, cy + 1);
                var h11 = tm.CornerHeight(cx + 1, cy + 1);
                var dx = ((h10 - h00) + (h11 - h01)) / 2f;
                var dy = ((h01 - h00) + (h11 - h10)) / 2f;

                var water = (tm.CornerType(cx, cy) == 1 ? 1 : 0) +
                            (tm.CornerType(cx + 1, cy) == 1 ? 1 : 0) +
                            (tm.CornerType(cx + 1, cy + 1) == 1 ? 1 : 0) +
                            (tm.CornerType(cx, cy + 1) == 1 ? 1 : 0);
                if (water >= 3)
                {
                    dx = 0;
                    dy = 0;
                }

                dx *= SlopeScale;
                dy *= SlopeScale;
                var len = MathF.Sqrt(dx * dx + dy * dy + 1);
                var nx = -dx / len;
                var ny = -dy / len;
                var nz = 1f / len;
                var i = (cy * w + cx) * 4;
                rgba[i] = (byte)((nx * 0.5f + 0.5f) * 255);
                rgba[i + 1] = (byte)((ny * 0.5f + 0.5f) * 255);
                rgba[i + 2] = (byte)((nz * 0.5f + 0.5f) * 255);
                rgba[i + 3] = 255;
            }
        }
    }
}
