using System;
using Godot;
using Starve.Core;
using TileMap = Starve.Core.TileMap;

namespace GodotClient.Game;

/// <summary>
/// 地形网格：2D 为平台+崖壁；3D 为细分双线性缓坡，法线从高度场差分。
/// </summary>
public static class MapMeshBuilder
{
    public const int ChunkTiles = 40;
    public const int DefaultSmoothSubdiv = 4;
    public const float DefaultWorldTiling = 0.22f;

    /// <summary>每格边分成几段。1 = 旧的单四边形；4 能看见双线性曲面。</summary>
    public static int SmoothSubdiv
    {
        get => _smoothSubdiv;
        set => _smoothSubdiv = Math.Clamp(value, 1, 8);
    }

    private static int _smoothSubdiv = DefaultSmoothSubdiv;

    /// <summary>无缝贴图在世界上的重复密度。越小一张图铺得越远，格子感越弱。</summary>
    public static float WorldTiling
    {
        get => _worldTiling;
        set => _worldTiling = Math.Clamp(value, 0.12f, 1f);
    }

    private static float _worldTiling = DefaultWorldTiling;

    public static ArrayMesh BuildChunk(TileMap tm, int cx0, int cy0, int cx1, int cy1, TileAtlasBuilder atlas) =>
        BuildChunk(tm, cx0, cy0, cx1, cy1, atlas, world3D: false);

    /// <summary>顶点放在 (wx, 视觉高度, wy)。COLOR 为 splat 权重，不依赖图集。</summary>
    public static ArrayMesh BuildChunk3D(TileMap tm, int cx0, int cy0, int cx1, int cy1, TileAtlasBuilder? atlas = null) =>
        BuildChunk(tm, cx0, cy0, cx1, cy1, atlas, world3D: true);

    private static ArrayMesh BuildChunk(
        TileMap tm, int cx0, int cy0, int cx1, int cy1, TileAtlasBuilder? atlas, bool world3D)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        var vertexCount = 0;

        for (var cy = cy0; cy < cy1; cy++)
        {
            for (var cx = cx0; cx < cx1; cx++)
            {
                var ao = 1f - TileAo(tm, cx, cy) * 0.5f;
                if (world3D)
                {
                    AddSmoothTile(st, tm, cx, cy, ao, ref vertexCount);
                    continue;
                }

                if (atlas is null)
                    throw new InvalidOperationException("2D 地形需要图集");

                foreach (var quad in SlopeMesh.BuildTile(tm, cx, cy))
                {
                    var kind = quad.Cliff ? SlopeMesh.CliffTerrainKind : quad.TerrainKind;
                    var rect = quad.Cliff
                        ? FirstVariant(atlas, kind)
                        : PickVariant(atlas, kind, cx, cy);
                    var tint = TintColor(quad.HeightAvg, quad.FaceSlope) * ao;
                    if (quad.Cliff) tint *= new Color(0.72f, 0.68f, 0.62f);

                    AddVert(st, quad.V0, rect, tint, cx, cy, quad, world3D: false);
                    AddVert(st, quad.V1, rect, tint, cx, cy, quad, world3D: false);
                    AddVert(st, quad.V2, rect, tint, cx, cy, quad, world3D: false);
                    AddVert(st, quad.V3, rect, tint, cx, cy, quad, world3D: false);

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
        }

        return st.Commit();
    }

    private static void AddSmoothTile(
        SurfaceTool st, TileMap tm, int cx, int cy, float ao, ref int vertexCount)
    {
        var n = SmoothSubdiv;
        var stride = n + 1;
        var baseIdx = vertexCount;
        for (var iy = 0; iy <= n; iy++)
        {
            var fy = iy / (float)n;
            for (var ix = 0; ix <= n; ix++)
            {
                var fx = ix / (float)n;
                var wx = cx + fx;
                var wy = cy + fy;
                var h = SlopeMesh.SampleHeightBilinear(tm, wx, wy);
                var p = IsoCamera3D.WorldTo3D(wx, wy, h);
                var (normal, _) = HeightNormalAndSlope(tm, wx, wy);
                var splat = SplatWeights(tm, wx, wy);
                st.SetColor(splat);
                st.SetUV(Vector2.Zero);
                st.SetUV2(new Vector2(ao, WaterWeight(tm, wx, wy)));
                st.SetNormal(normal);
                st.AddVertex(new Vector3(p.X, p.Y, p.Z));
            }
        }

        vertexCount += stride * stride;
        for (var iy = 0; iy < n; iy++)
        {
            for (var ix = 0; ix < n; ix++)
            {
                var i00 = baseIdx + iy * stride + ix;
                var i10 = i00 + 1;
                var i01 = i00 + stride;
                var i11 = i01 + 1;
                // 俯视顺时针：X+ 再 Z+，与旧四边形一致。
                st.AddIndex(i00);
                st.AddIndex(i10);
                st.AddIndex(i11);
                st.AddIndex(i00);
                st.AddIndex(i11);
                st.AddIndex(i01);
            }
        }
    }

    /// <summary>
    /// 四角类型双线性权重。COLOR = (草, 沙/土, 岩, 雪)。
    /// 类型：1 水 2 沙 3 草 4 岩 5 雪。
    /// </summary>
    private static Color SplatWeights(TileMap tm, float wx, float wy)
    {
        var x0 = (int)MathF.Floor(wx);
        var y0 = (int)MathF.Floor(wy);
        var fx = wx - x0;
        var fy = wy - y0;
        var grass = 0f;
        var dirt = 0f;
        var rock = 0f;
        var snow = 0f;
        AccumulateType(tm.CornerType(x0, y0), (1 - fx) * (1 - fy), ref grass, ref dirt, ref rock, ref snow);
        AccumulateType(tm.CornerType(x0 + 1, y0), fx * (1 - fy), ref grass, ref dirt, ref rock, ref snow);
        AccumulateType(tm.CornerType(x0, y0 + 1), (1 - fx) * fy, ref grass, ref dirt, ref rock, ref snow);
        AccumulateType(tm.CornerType(x0 + 1, y0 + 1), fx * fy, ref grass, ref dirt, ref rock, ref snow);
        var sum = grass + dirt + rock;
        if (sum < 1e-4f)
            return new Color(0f, 0f, 0f, snow);
        return new Color(grass / sum, dirt / sum, rock / sum, snow);
    }

    private static void AccumulateType(
        int type, float w, ref float grass, ref float dirt, ref float rock, ref float snow)
    {
        switch (type)
        {
            case 2:
                dirt += w;
                break;
            case 4:
                rock += w;
                break;
            case 5:
                snow += w;
                break;
            case 1:
                break;
            default:
                grass += w;
                break;
        }
    }

    private static float WaterWeight(TileMap tm, float wx, float wy)
    {
        var x0 = (int)MathF.Floor(wx);
        var y0 = (int)MathF.Floor(wy);
        var fx = wx - x0;
        var fy = wy - y0;
        float T(int x, int y) => tm.CornerType(x, y) == 1 ? 1f : 0f;
        return T(x0, y0) * (1 - fx) * (1 - fy)
            + T(x0 + 1, y0) * fx * (1 - fy)
            + T(x0, y0 + 1) * (1 - fx) * fy
            + T(x0 + 1, y0 + 1) * fx * fy;
    }

    /// <summary>高度场中心差分法线；slope 是逻辑高差/水平距，用来混岩石。</summary>
    private static (Vector3 Normal, float Slope) HeightNormalAndSlope(TileMap tm, float wx, float wy)
    {
        const float e = 0.12f;
        var dhx = SlopeMesh.SampleHeightBilinear(tm, wx + e, wy)
            - SlopeMesh.SampleHeightBilinear(tm, wx - e, wy);
        var dhy = SlopeMesh.SampleHeightBilinear(tm, wx, wy + e)
            - SlopeMesh.SampleHeightBilinear(tm, wx, wy - e);
        var s = IsoCamera3D.HeightScale / (2f * e);
        var slope = MathF.Sqrt(dhx * dhx + dhy * dhy) / (2f * e);
        return (new Vector3(-dhx * s, 1f, -dhy * s).Normalized(), slope);
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

    /// <summary>高度/坡度着色：按视觉坡度变暗，越高越偏冷色。</summary>
    private static Color TintColor(float hAvg, float slope)
    {
        var visual = slope * IsoCamera3D.HeightScale;
        var bright = Mathf.Clamp(1f - visual * 0.12f, 0.82f, 1.06f);
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
