using System;
using System.Collections.Generic;
using Godot;

namespace GodotClient.Game;

/// <summary>
/// 地形贴图图集：把 sheet-cut 菱形切图预处理（裁菱形 → 旋转 45° 填满 128×128）
/// 打包进一张图集，供地形网格按 UV 采样；水/未知用程序 value-noise 贴图。
/// 变体映射与 web 端 terrain-splat.ts 保持一致。
/// </summary>
public sealed class TileAtlasBuilder
{
    public const int TileSize = 128;
    public const int AtlasCols = 6;

    private static readonly Dictionary<int, int[]> VariantFiles = new()
    {
        [2] = new[] { 42, 43, 44, 45 }, // 沙
        [3] = new[] { 31, 33, 37, 39 }, // 草
        [4] = new[] { 69, 70, 71, 78, 79, 80, 81, 82 }, // 岩
        [5] = new[] { 61, 64, 67, 72 }, // 雪
    };

    public ImageTexture Atlas { get; private set; } = null!;

    /// <summary>类型 → 变体 UV 矩形（图集内）。</summary>
    public Dictionary<int, Rect2[]> TypeVariants { get; } = new();

    public static TileAtlasBuilder Build()
    {
        var builder = new TileAtlasBuilder();
        var rows = 4;
        var atlas = Image.CreateEmpty(AtlasCols * TileSize, rows * TileSize, false, Image.Format.Rgba8);
        var idx = 0;
        for (var k = 0; k < 6; k++)
        {
            var files = VariantFiles.TryGetValue(k, out var f) ? f : new[] { -1 };
            var rects = new List<Rect2>();
            foreach (var file in files)
            {
                var img = file >= 0 ? LoadDiamondFill(file) : MakeProcedural(k, idx);
                var cell = new Vector2I((idx % AtlasCols) * TileSize, (idx / AtlasCols) * TileSize);
                atlas.BlitRect(img, new Rect2I(0, 0, TileSize, TileSize), cell);
                rects.Add(new Rect2(
                    cell.X / (float)atlas.GetWidth(),
                    cell.Y / (float)atlas.GetHeight(),
                    TileSize / (float)atlas.GetWidth(),
                    TileSize / (float)atlas.GetHeight()));
                idx++;
            }
            builder.TypeVariants[k] = rects.ToArray();
        }
        builder.Atlas = ImageTexture.CreateFromImage(atlas);
        return builder;
    }

    /// <summary>源菱形四角（各边中点）→ 128×128 画布四角（与 web 端矩阵一致）。</summary>
    private static Image LoadDiamondFill(int file)
    {
        var tex = GD.Load<Texture2D>($"res://assets/tiles/tile_{file:000}.png");
        var src = tex.GetImage();
        var w = src.GetWidth();
        var h = src.GetHeight();
        var dst = Image.CreateEmpty(TileSize, TileSize, false, Image.Format.Rgba8);
        for (var sy = 0; sy < TileSize; sy++)
        {
            for (var sx = 0; sx < TileSize; sx++)
            {
                var u = (sx - sy + TileSize) * w / (2f * TileSize);
                var v = (sx + sy) * h / (2f * TileSize);
                dst.SetPixel(sx, sy, src.GetPixel(Mathf.Clamp((int)u, 0, w - 1), Mathf.Clamp((int)v, 0, h - 1)));
            }
        }
        return dst;
    }

    private static Image MakeProcedural(int kind, int seed)
    {
        var rand = new Mulberry32((uint)(seed * 7919 + kind));
        var noise = TileableNoise(TileSize, 8, rand);
        var img = Image.CreateEmpty(TileSize, TileSize, false, Image.Format.Rgba8);
        for (var y = 0; y < TileSize; y++)
        {
            for (var x = 0; x < TileSize; x++)
            {
                var n = noise[y * TileSize + x];
                float r, g, b;
                if (kind == 1)
                {
                    var wave = Mathf.Sin(y * 0.35f + n * 3.2f) * 0.5f + 0.5f;
                    r = 43 + n * 12;
                    g = 108 + n * 14;
                    b = 176 + wave * 22;
                }
                else
                {
                    r = g = b = 0x66;
                }
                img.SetPixel(x, y, new Color(r / 255f, g / 255f, b / 255f, 1));
            }
        }
        return img;
    }

    private static float[] TileableNoise(int size, int grid, Mulberry32 rand)
    {
        var cells = new float[grid * grid];
        for (var i = 0; i < cells.Length; i++) cells[i] = rand.Next();
        var outArr = new float[size * size];
        var scale = grid / (float)size;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var gxf = x * scale;
                var gyf = y * scale;
                var x0 = (int)Mathf.Floor(gxf) % grid;
                var y0 = (int)Mathf.Floor(gyf) % grid;
                var x1 = (x0 + 1) % grid;
                var y1 = (y0 + 1) % grid;
                var fx = gxf - Mathf.Floor(gxf);
                var fy = gyf - Mathf.Floor(gyf);
                var sx = fx * fx * (3 - 2 * fx);
                var sy = fy * fy * (3 - 2 * fy);
                var a = cells[y0 * grid + x0];
                var b = cells[y0 * grid + x1];
                var c = cells[y1 * grid + x0];
                var d = cells[y1 * grid + x1];
                outArr[y * size + x] = a + (b - a) * sx + (c - a) * sy + (a - b - c + d) * sx * sy;
            }
        }
        return outArr;
    }

    private sealed class Mulberry32
    {
        private uint _a;

        public Mulberry32(uint seed) => _a = seed;

        public float Next()
        {
            _a += 0x6d2b79f5u;
            var t = _a;
            t = ((t ^ (t >> 15)) * (t | 1u));
            t ^= t + ((t ^ (t >> 7)) * (t | 61u));
            return ((t ^ (t >> 14)) & 0xffffffffu) / 4294967296f;
        }
    }
}
