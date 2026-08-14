using Starve.Game.V1;

namespace Starve.Core;

/// <summary>
/// TileMap：地形高度场（角粒度）的只读访问。纯逻辑、无渲染依赖。
/// 角索引：cornerW = width+1，数据行优先排列。
/// </summary>
public sealed class TileMap
{
    public int Width { get; }
    public int Height { get; }
    public int CornerW { get; }
    public int CornerH { get; }

    private readonly byte[] _heights;
    private readonly byte[] _types;

    public TileMap(MapConfig cfg)
    {
        Width = cfg.Width;
        Height = cfg.Height;
        CornerW = Width + 1;
        CornerH = Height + 1;
        _heights = cfg.CornerHeights.ToByteArray();
        _types = cfg.CornerTypes.ToByteArray();
    }

    /// <summary>角高度（0~255；越界返回 0）。</summary>
    public float CornerHeight(int cx, int cy)
    {
        if (cx < 0 || cy < 0 || cx >= CornerW || cy >= CornerH) return 0;
        var i = cy * CornerW + cx;
        return i < _heights.Length ? _heights[i] : 0;
    }

    /// <summary>角地形类型（TerrainType 枚举值；越界返回 0）。</summary>
    public int CornerType(int cx, int cy)
    {
        if (cx < 0 || cy < 0 || cx >= CornerW || cy >= CornerH) return 0;
        var i = cy * CornerW + cx;
        return i < _types.Length ? _types[i] : 0;
    }

    /// <summary>地面高度查询：所在格子四角双线性插值。</summary>
    public float HeightAt(float wx, float wy)
    {
        var x0 = (int)MathF.Floor(wx);
        var y0 = (int)MathF.Floor(wy);
        var fx = wx - x0;
        var fy = wy - y0;
        var h00 = CornerHeight(x0, y0);
        var h10 = CornerHeight(x0 + 1, y0);
        var h01 = CornerHeight(x0, y0 + 1);
        var h11 = CornerHeight(x0 + 1, y0 + 1);
        return h00 + (h10 - h00) * fx + (h01 + (h11 - h01) * fx - (h00 + (h10 - h00) * fx)) * fy;
    }

    /// <summary>画家排序深度：wx + wy + 高度（越高越靠前/靠上）。</summary>
    public float DepthAt(float wx, float wy) => wx + wy + HeightAt(wx, wy);
}
