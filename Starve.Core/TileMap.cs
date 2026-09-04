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

    /// <summary>true：高度用四角双线性（3D 缓坡）；false：2D 平台+崖壁带。</summary>
    public bool SmoothSlopes { get; set; }

    private readonly byte[] _heights;
    private readonly byte[] _types;

    public TileMap(MapConfig cfg)
        : this(cfg.Width, cfg.Height, cfg.CornerHeights.ToByteArray(), cfg.CornerTypes.ToByteArray())
    {
    }

    public TileMap(int width, int height, byte[] heights, byte[] types)
    {
        Width = width;
        Height = height;
        CornerW = Width + 1;
        CornerH = Height + 1;
        _heights = heights;
        _types = types;
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

    /// <summary>地面高度：SmoothSlopes 时四角双线性，否则高差 ≥ 1 收成崖壁带。</summary>
    public float HeightAt(float wx, float wy) =>
        SmoothSlopes
            ? SlopeMesh.SampleHeightBilinear(this, wx, wy)
            : SlopeMesh.SampleHeight(this, wx, wy);

    /// <summary>画家排序深度：wx + wy + 高度（越高越靠前/靠上）。</summary>
    public float DepthAt(float wx, float wy) => wx + wy + HeightAt(wx, wy);
}
