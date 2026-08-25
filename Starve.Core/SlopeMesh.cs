using System.Numerics;

namespace Starve.Core;

/// <summary>
/// 坡度网格：地面用世界 XY 连续 UV；相邻角高差 ≥ 1 时把落差收进窄崖壁带。
/// 边高度只由该边两端决定，共享边两侧一致，网格水密。
/// </summary>
public static class SlopeMesh
{
    /// <summary>达到该高差（世界格）时改为崖壁，不再铺成斜面。</summary>
    public const float CliffThreshold = 1f;

    /// <summary>崖壁占一格边长的比例；其余为平台。</summary>
    public const float CliffBand = 0.22f;

    public const int CliffTerrainKind = 4; // 岩

    public static float EdgeHeight(float h0, float h1, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        var dh = h1 - h0;
        if (MathF.Abs(dh) < CliffThreshold - 1e-3f)
            return h0 + dh * t;

        if (dh < 0)
        {
            var start = 1f - CliffBand;
            if (t <= start) return h0;
            return h0 + dh * ((t - start) / CliffBand);
        }

        if (t >= CliffBand) return h1;
        return h0 + dh * (t / CliffBand);
    }

    public static float SampleHeight(TileMap tm, float wx, float wy)
    {
        if (tm.Width <= 0 || tm.Height <= 0) return 0;

        var x0 = (int)MathF.Floor(wx);
        var y0 = (int)MathF.Floor(wy);
        if (x0 < 0) { x0 = 0; wx = 0; }
        else if (x0 >= tm.Width) { x0 = tm.Width - 1; wx = tm.Width; }
        if (y0 < 0) { y0 = 0; wy = 0; }
        else if (y0 >= tm.Height) { y0 = tm.Height - 1; wy = tm.Height; }

        var fx = wx - x0;
        var fy = wy - y0;
        var h00 = tm.CornerHeight(x0, y0);
        var h10 = tm.CornerHeight(x0 + 1, y0);
        var h01 = tm.CornerHeight(x0, y0 + 1);
        var h11 = tm.CornerHeight(x0 + 1, y0 + 1);

        if (WaterCorners(tm, x0, y0) >= 3)
            return MathF.Max(MathF.Max(h00, h10), MathF.Max(h01, h11));

        return HeightOnTile(h00, h10, h01, h11, fx, fy);
    }

    public static float HeightOnTile(float h00, float h10, float h01, float h11, float fx, float fy)
    {
        var hN = EdgeHeight(h00, h10, fx);
        var hS = EdgeHeight(h01, h11, fx);
        return EdgeHeight(hN, hS, fy);
    }

    public static int WaterCorners(TileMap tm, int cx, int cy) =>
        (tm.CornerType(cx, cy) == 1 ? 1 : 0) +
        (tm.CornerType(cx + 1, cy) == 1 ? 1 : 0) +
        (tm.CornerType(cx + 1, cy + 1) == 1 ? 1 : 0) +
        (tm.CornerType(cx, cy + 1) == 1 ? 1 : 0);

    public static int DominantType(TileMap tm, int cx, int cy)
    {
        Span<int> types =
        [
            tm.CornerType(cx, cy),
            tm.CornerType(cx + 1, cy),
            tm.CornerType(cx + 1, cy + 1),
            tm.CornerType(cx, cy + 1),
        ];
        var best = types[0];
        var bestCount = 0;
        for (var i = 0; i < 4; i++)
        {
            var c = 0;
            for (var j = 0; j < 4; j++)
                if (types[j] == types[i]) c++;
            if (c > bestCount)
            {
                best = types[i];
                bestCount = c;
            }
        }
        return best;
    }

    public static IReadOnlyList<SlopeQuad> BuildTile(TileMap tm, int cx, int cy)
    {
        var h00 = tm.CornerHeight(cx, cy);
        var h10 = tm.CornerHeight(cx + 1, cy);
        var h01 = tm.CornerHeight(cx, cy + 1);
        var h11 = tm.CornerHeight(cx + 1, cy + 1);
        var hMax = MathF.Max(MathF.Max(h00, h10), MathF.Max(h01, h11));
        var hMin = MathF.Min(MathF.Min(h00, h10), MathF.Min(h01, h11));
        var water = WaterCorners(tm, cx, cy) >= 3;
        var kind = water ? 1 : DominantType(tm, cx, cy);

        if (water)
        {
            return
            [
                MakeQuad(
                    cx, cy, 0, 0, 1, 1,
                    hMax, hMax, hMax, hMax,
                    kind, cliff: false, water: true, hMin, hMax),
            ];
        }

        var xs = AxisSamples(h00, h10, h01, h11);
        var ys = AxisSamples(h00, h01, h10, h11);
        var quads = new List<SlopeQuad>((xs.Length - 1) * (ys.Length - 1));
        for (var j = 0; j < ys.Length - 1; j++)
        {
            for (var i = 0; i < xs.Length - 1; i++)
            {
                var x0 = xs[i];
                var x1 = xs[i + 1];
                var y0 = ys[j];
                var y1 = ys[j + 1];
                var q00 = HeightOnTile(h00, h10, h01, h11, x0, y0);
                var q10 = HeightOnTile(h00, h10, h01, h11, x1, y0);
                var q01 = HeightOnTile(h00, h10, h01, h11, x0, y1);
                var q11 = HeightOnTile(h00, h10, h01, h11, x1, y1);
                var drop = MathF.Max(MathF.Max(q00, q10), MathF.Max(q01, q11))
                           - MathF.Min(MathF.Min(q00, q10), MathF.Min(q01, q11));
                var cliff = drop >= CliffThreshold * 0.5f;
                quads.Add(MakeQuad(
                    cx, cy, x0, y0, x1, y1,
                    q00, q10, q11, q01,
                    kind, cliff, water: false, hMin, hMax));
            }
        }
        return quads;
    }

    private static SlopeQuad MakeQuad(
        int cx, int cy,
        float fx0, float fy0, float fx1, float fy1,
        float h00, float h10, float h11, float h01,
        int kind, bool cliff, bool water, float tileMin, float tileMax)
    {
        return new SlopeQuad(
            Vert(cx + fx0, cy + fy0, h00, cliff),
            Vert(cx + fx1, cy + fy0, h10, cliff),
            Vert(cx + fx1, cy + fy1, h11, cliff),
            Vert(cx + fx0, cy + fy1, h01, cliff),
            kind, cliff, water, tileMin, tileMax);
    }

    private static SlopeVertex Vert(float wx, float wy, float height, bool cliff)
    {
        var local = IsoMath.WorldToLocal(wx, wy, height);
        return new SlopeVertex(wx, wy, height, local.X, local.Y, cliff);
    }

    /// <summary>沿该轴两条对边，从 h*0 走到 h*1。</summary>
    private static float[] AxisSamples(float a0, float a1, float b0, float b1)
    {
        var pts = new List<float>(4) { 0f, 1f };
        AddSplit(pts, a0, a1);
        AddSplit(pts, b0, b1);
        pts.Sort();
        var unique = new List<float>(pts.Count) { pts[0] };
        for (var i = 1; i < pts.Count; i++)
        {
            if (pts[i] - unique[^1] > 1e-4f)
                unique.Add(pts[i]);
        }
        return unique.ToArray();
    }

    private static void AddSplit(List<float> pts, float h0, float h1)
    {
        var dh = h1 - h0;
        if (MathF.Abs(dh) < CliffThreshold - 1e-3f) return;
        pts.Add(dh < 0 ? 1f - CliffBand : CliffBand);
    }
}

public readonly struct SlopeVertex
{
    public SlopeVertex(float wx, float wy, float height, float localX, float localY, bool cliff)
    {
        Wx = wx;
        Wy = wy;
        Height = height;
        LocalX = localX;
        LocalY = localY;
        Cliff = cliff;
    }

    public float Wx { get; }
    public float Wy { get; }
    public float Height { get; }
    public float LocalX { get; }
    public float LocalY { get; }
    public bool Cliff { get; }

    public Vector2 Local => new(LocalX, LocalY);
}

public readonly struct SlopeQuad
{
    public SlopeQuad(
        SlopeVertex v0, SlopeVertex v1, SlopeVertex v2, SlopeVertex v3,
        int terrainKind, bool cliff, bool water, float tileHeightMin, float tileHeightMax)
    {
        V0 = v0;
        V1 = v1;
        V2 = v2;
        V3 = v3;
        TerrainKind = terrainKind;
        Cliff = cliff;
        Water = water;
        TileHeightMin = tileHeightMin;
        TileHeightMax = tileHeightMax;
    }

    public SlopeVertex V0 { get; }
    public SlopeVertex V1 { get; }
    public SlopeVertex V2 { get; }
    public SlopeVertex V3 { get; }
    public int TerrainKind { get; }
    public bool Cliff { get; }
    public bool Water { get; }
    public float TileHeightMin { get; }
    public float TileHeightMax { get; }

    public float HeightAvg => (V0.Height + V1.Height + V2.Height + V3.Height) * 0.25f;

    public float FaceSlope =>
        MathF.Max(MathF.Max(V0.Height, V1.Height), MathF.Max(V2.Height, V3.Height))
        - MathF.Min(MathF.Min(V0.Height, V1.Height), MathF.Min(V2.Height, V3.Height));
}
