using System;
using System.Linq;
using Godot;
using Starve.Core;
using TileMap = Starve.Core.TileMap;

namespace GodotClient.Game;

/// <summary>
/// 地形网格构建：按角类型选图集变体（确定性哈希），四角真实高度 + 菱形 UV，
/// 顶点色烘焙 高度/坡度着色 + AO（公式与 web 端 terrain-splat.ts 一致）。
/// </summary>
public static class MapMeshBuilder
{
    public const int ChunkTiles = 40;

    public static ArrayMesh BuildChunk(TileMap tm, int cx0, int cy0, int cx1, int cy1, TileAtlasBuilder atlas)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        var vertexCount = 0;

        for (var cy = cy0; cy < cy1; cy++)
        {
            for (var cx = cx0; cx < cx1; cx++)
            {
                var hs = new[]
                {
                    tm.CornerHeight(cx, cy),
                    tm.CornerHeight(cx + 1, cy),
                    tm.CornerHeight(cx + 1, cy + 1),
                    tm.CornerHeight(cx, cy + 1),
                };
                var types = new[]
                {
                    tm.CornerType(cx, cy),
                    tm.CornerType(cx + 1, cy),
                    tm.CornerType(cx + 1, cy + 1),
                    tm.CornerType(cx, cy + 1),
                };

                var water = types.Count(t => t == 1);
                var hMax = hs.Max();
                var hAvg = hs.Average();
                var slope = hMax - hs.Min();

                var kind = water >= 3 ? 1 : DominantType(types);
                var rect = PickVariant(atlas, kind, cx, cy);
                var tint = TintColor(hAvg, slope) * (1f - TileAo(tm, cx, cy) * 0.5f);

                Godot.Vector2 p0, p1, p2, p3;
                if (water >= 3)
                {
                    // 水：四角统一到最高角，湖面平整
                    var flat = ToGodot(IsoMath.WorldToLocal(cx, cy, hMax));
                    p0 = flat;
                    p1 = flat + new Vector2(20, 10);
                    p2 = flat + new Vector2(0, 20);
                    p3 = flat + new Vector2(-20, 10);
                }
                else
                {
                    p0 = ToGodot(IsoMath.WorldToLocal(cx, cy, hs[0]));
                    p1 = ToGodot(IsoMath.WorldToLocal(cx + 1, cy, hs[1]));
                    p2 = ToGodot(IsoMath.WorldToLocal(cx + 1, cy + 1, hs[2]));
                    p3 = ToGodot(IsoMath.WorldToLocal(cx, cy + 1, hs[3]));
                }

                // 菱形 UV：画布四边中点 = 菱形四角
                var uv0 = UvInRect(rect, 0.5f, 0f);
                var uv1 = UvInRect(rect, 1f, 0.5f);
                var uv2 = UvInRect(rect, 0.5f, 1f);
                var uv3 = UvInRect(rect, 0f, 0.5f);

                st.SetColor(tint);
                st.SetUV(uv0);
                st.AddVertex(ToV3(p0));
                st.SetUV(uv1);
                st.AddVertex(ToV3(p1));
                st.SetUV(uv2);
                st.AddVertex(ToV3(p2));
                st.SetUV(uv3);
                st.AddVertex(ToV3(p3));

                var baseIdx = vertexCount;
                vertexCount += 4;
                st.AddIndex(baseIdx);
                st.AddIndex(baseIdx + 1);
                st.AddIndex(baseIdx + 2);
                st.AddIndex(baseIdx);
                st.AddIndex(baseIdx + 2);
                st.AddIndex(baseIdx + 3);
            }
        }

        return st.Commit();
    }

    private static Rect2 PickVariant(TileAtlasBuilder atlas, int kind, int cx, int cy)
    {
        if (!atlas.TypeVariants.TryGetValue(kind, out var list) || list.Length == 0)
            return new Rect2(0, 0, 1, 1);
        var rng = new Mulberry32((uint)(cx * 73856093 ^ cy * 19349663 ^ kind * 83492791));
        return list[Mathf.FloorToInt(rng.Next() * list.Length)];
    }

    /// <summary>高度/坡度着色：坡越陡越暗，越高越偏冷色。</summary>
    private static Color TintColor(float hAvg, float slope)
    {
        var bright = Mathf.Clamp(1f - slope * 0.055f, 0.76f, 1.06f);
        var t = Mathf.Clamp((hAvg - 1) / 8f, 0, 1);
        var r = Mathf.Clamp(255f * bright * (1 - t * 0.08f + 0.03f), 0, 255) / 255f;
        var g = Mathf.Clamp(255f * bright * (1 - t * 0.02f), 0, 255) / 255f;
        var b = Mathf.Clamp(255f * bright * (1 + t * 0.14f - 0.02f), 0, 255) / 255f;
        return new Color(r, g, b);
    }

    /// <summary>单格 AO：半径 3 内"比自己高"的角，高度差 × 距离衰减累加（K=22）。</summary>
    private static float TileAo(TileMap tm, int cx, int cy)
    {
        const int r = 3;
        const float k = 22;
        var ao = 0f;
        foreach (var (ox, oy) in new[] { (0, 0), (1, 0), (1, 1), (0, 1) })
        {
            var x = cx + ox;
            var y = cy + oy;
            var hc = tm.CornerHeight(x, y);
            var occ = 0f;
            for (var ny = Math.Max(0, y - r); ny <= Math.Min(tm.CornerH - 1, y + r); ny++)
            {
                for (var nx = Math.Max(0, x - r); nx <= Math.Min(tm.CornerW - 1, x + r); nx++)
                {
                    var d = Math.Max(Math.Abs(nx - x), Math.Abs(ny - y));
                    if (d == 0) continue;
                    var dh = tm.CornerHeight(nx, ny) - hc;
                    if (dh > 0) occ += dh * (1 - (d - 1) / (float)r);
                }
            }
            ao += Mathf.Min(1, occ / k);
        }
        return ao / 4f;
    }

    private static int DominantType(int[] types)
    {
        var best = types[0];
        var bestCount = 0;
        foreach (var t in types)
        {
            var c = types.Count(x => x == t);
            if (c > bestCount)
            {
                best = t;
                bestCount = c;
            }
        }
        return best;
    }

    private static Vector2 UvInRect(Rect2 rect, float u, float v) =>
        new(rect.Position.X + u * rect.Size.X, rect.Position.Y + v * rect.Size.Y);

    private static Godot.Vector2 ToGodot(System.Numerics.Vector2 v) => new(v.X, v.Y);

    private static Godot.Vector3 ToV3(Godot.Vector2 v) => new(v.X, v.Y, 0);

    private sealed class Mulberry32
    {
        private uint _a;

        public Mulberry32(uint seed) => _a = seed;

        public float Next()
        {
            _a += 0x6d2b79f5u;
            var t = _a;
            t = (t ^ (t >> 15)) * (t | 1u);
            t ^= t + ((t ^ (t >> 7)) * (t | 61u));
            return ((t ^ (t >> 14)) & 0xffffffffu) / 4294967296f;
        }
    }
}
