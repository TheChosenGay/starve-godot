using System;
using Godot;
using Starve.Core;
using TileMap = Starve.Core.TileMap;

namespace GodotClient.Game;

/// <summary>
/// 地形网格：2D 为平台+崖壁；3D 为四角双线性缓坡。顶点色烘焙高度/AO。
/// </summary>
public static class MapMeshBuilder
{
    public const int ChunkTiles = 40;

    public static ArrayMesh BuildChunk(TileMap tm, int cx0, int cy0, int cx1, int cy1, TileAtlasBuilder atlas) =>
        BuildChunk(tm, cx0, cy0, cx1, cy1, atlas, world3D: false);

    /// <summary>同一套 UV/顶点色，顶点放在 (wx, height, wy)；缓坡、正面朝上。</summary>
    public static ArrayMesh BuildChunk3D(TileMap tm, int cx0, int cy0, int cx1, int cy1, TileAtlasBuilder atlas) =>
        BuildChunk(tm, cx0, cy0, cx1, cy1, atlas, world3D: true);

    private static ArrayMesh BuildChunk(
        TileMap tm, int cx0, int cy0, int cx1, int cy1, TileAtlasBuilder atlas, bool world3D)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        var vertexCount = 0;

        for (var cy = cy0; cy < cy1; cy++)
        {
            for (var cx = cx0; cx < cx1; cx++)
            {
                var ao = 1f - TileAo(tm, cx, cy) * 0.5f;
                foreach (var quad in world3D
                    ? SlopeMesh.BuildTileSmooth(tm, cx, cy)
                    : SlopeMesh.BuildTile(tm, cx, cy))
                {
                    var kind = quad.Cliff ? SlopeMesh.CliffTerrainKind : quad.TerrainKind;
                    var rect = quad.Cliff
                        ? FirstVariant(atlas, kind)
                        : PickVariant(atlas, kind, cx, cy);
                    var tint = TintColor(quad.HeightAvg, quad.FaceSlope) * ao;
                    if (quad.Cliff) tint *= new Color(0.72f, 0.68f, 0.62f);

                    AddVert(st, quad.V0, rect, tint, cx, cy, quad, world3D);
                    AddVert(st, quad.V1, rect, tint, cx, cy, quad, world3D);
                    AddVert(st, quad.V2, rect, tint, cx, cy, quad, world3D);
                    AddVert(st, quad.V3, rect, tint, cx, cy, quad, world3D);

                    var baseIdx = vertexCount;
                    vertexCount += 4;
                    // Godot 正面是顺时针：俯视时 V0→V1→V2（X+ 再 Z+）才朝上。
                    st.AddIndex(baseIdx);
                    st.AddIndex(baseIdx + 1);
                    st.AddIndex(baseIdx + 2);
                    st.AddIndex(baseIdx);
                    st.AddIndex(baseIdx + 2);
                    st.AddIndex(baseIdx + 3);
                }
            }
        }

        if (world3D) st.GenerateNormals(true);
        return st.Commit();
    }

    private static void AddVert(
        SurfaceTool st, SlopeVertex v, Rect2 rect, Color tint, int cx, int cy, SlopeQuad quad, bool world3D)
    {
        Vector2 uv;
        if (quad.Cliff)
        {
            var span = MathF.Max(quad.TileHeightMax - quad.TileHeightMin, 0.001f);
            var t = Math.Clamp((quad.TileHeightMax - v.Height) / span, 0f, 1f);
            uv = UvInRect(rect, Fract(v.Wx + v.Wy), t);
        }
        else
        {
            // 世界 UV：一格对应整张无缝贴图。子格用相对父格的 0..1，插值不跨 fract 缝。
            uv = UvInRect(rect, v.Wx - cx, v.Wy - cy);
        }

        st.SetColor(tint);
        st.SetUV(uv);
        if (world3D)
        {
            var p = IsoCamera3D.WorldTo3D(v.Wx, v.Wy, v.Height);
            st.AddVertex(new Vector3(p.X, p.Y, p.Z));
        }
        else
        {
            st.AddVertex(new Vector3(v.LocalX, v.LocalY, 0));
        }
    }

    private static Rect2 FirstVariant(TileAtlasBuilder atlas, int kind)
    {
        if (!atlas.TypeVariants.TryGetValue(kind, out var list) || list.Length == 0)
            return new Rect2(0, 0, 1, 1);
        return list[0];
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

    private static Vector2 UvInRect(Rect2 rect, float u, float v) =>
        new(rect.Position.X + u * rect.Size.X, rect.Position.Y + v * rect.Size.Y);

    private static float Fract(float v) => v - MathF.Floor(v);

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
