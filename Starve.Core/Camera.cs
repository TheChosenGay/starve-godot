using System;
using System.Numerics;

namespace Starve.Core;

/// <summary>
/// 视口相机：世界坐标 ↔ 屏幕坐标、缩放、拖拽平移。
/// 纯逻辑，零渲染依赖（Godot/Cocos 渲染层注入高度查询即可复用）。
/// 菱形（等距）投影：screenX = (wx-wy)*20*zoom，screenY = (wx+wy)*10*zoom - 高度*20*zoom。
/// </summary>
public sealed class Camera
{
    public const int DefaultViewRadius = 24;
    public const int DefaultViewRadiusMax = 32;

    private float _zoom = 1;
    private bool _following;
    private float _followX;
    private float _followY;
    private float _smoothX;
    private float _smoothY;
    private float _panX;
    private float _panY;
    private readonly float _base;
    private readonly float _minFloor;
    private readonly float _maxFloor;
    private float _min;
    private float _max;
    private int _viewRadius = DefaultViewRadius;
    private int _viewRadiusMax = DefaultViewRadius;

    /// <summary>地面高度查询（菱形投影下 screenY 减去高度×step，由地形注入）。</summary>
    public Func<float, float, float>? HeightAt { get; set; }

    public Camera(float baseScale = 40, float minZoom = 0.4f, float maxZoom = 3f)
    {
        _base = baseScale;
        _minFloor = minZoom;
        _maxFloor = maxZoom;
        _min = minZoom;
        _max = maxZoom;
    }

    /// <summary>当前世界→屏幕比例（baseScale × zoom）。</summary>
    public float Scale => _base * _zoom;

    public float ZoomLevel => _zoom;

    public float MinZoom => _min;

    public float MaxZoom => _max;

    public int ViewRadius => _viewRadius;

    public int ViewRadiusMax => _viewRadiusMax;

    /// <summary>
    /// 服务端快照视野下限（切比雪夫格数）。与服务端 NormalizeViewRadius 一致：
    /// 0 → 默认 24；负数 → -1（不裁剪，不限制相机）。
    /// 未同时设置上限时，上限等于下限。
    /// </summary>
    public void SetViewRadius(int radius) => SetViewRange(radius, 0);

    /// <summary>
    /// 相机半径范围：radius 拉近下限，radiusMax 拉远上限（最大加载）。
    /// 0 上限 = 等于下限；任一为负 = 不裁剪。
    /// </summary>
    public void SetViewRange(int radius, int radiusMax)
    {
        if (radius < 0 || radiusMax < 0)
        {
            _viewRadius = -1;
            _viewRadiusMax = -1;
            return;
        }

        _viewRadius = radius == 0 ? DefaultViewRadius : radius;
        _viewRadiusMax = radiusMax == 0 ? _viewRadius : Math.Max(_viewRadius, radiusMax);
    }

    /// <summary>
    /// 按当前视口把缩放/拖拽限制在服务端视野内：屏幕四角相对玩家不超过 view_radius_max。
    /// 若上限大于下限，拉近也不会小于 view_radius。
    /// </summary>
    public void SyncToViewport(float viewW, float viewH)
    {
        if (_viewRadiusMax < 0)
        {
            _min = _minFloor;
            _max = _maxFloor;
            SetZoom(_zoom);
            return;
        }

        var halfAtOne = ViewportChebyshev(viewW, viewH, 1f);
        _min = MathF.Max(_minFloor, halfAtOne / _viewRadiusMax);
        if (_viewRadius > 0 && _viewRadius < _viewRadiusMax)
        {
            // 范围模式：拉近停在 view_radius，服务端范围优先于本地 maxZoom。
            _max = MathF.Max(_min, halfAtOne / _viewRadius);
        }
        else
        {
            // 上下限相同：只限制拉远，仍允许在本地 maxZoom 内继续拉近。
            _max = MathF.Max(_maxFloor, _min);
        }
        SetZoom(_zoom);
        ClampPan(viewW, viewH);
    }

    /// <summary>当前缩放下，屏幕四角相对相机中心的最大切比雪夫格数。</summary>
    public static float ViewportChebyshev(float viewW, float viewH, float zoom)
    {
        if (zoom <= 0)
        {
            return float.PositiveInfinity;
        }

        float maxCheb = 0;
        ReadOnlySpan<(float X, float Y)> corners =
        [
            (0, 0),
            (viewW, 0),
            (0, viewH),
            (viewW, viewH),
        ];
        foreach (var (sx, sy) in corners)
        {
            var a = (sx - viewW / 2) / (20 * zoom);
            var b = (sy - viewH / 2) / (10 * zoom);
            var dx = (a + b) / 2;
            var dy = (b - a) / 2;
            maxCheb = MathF.Max(maxCheb, MathF.Max(MathF.Abs(dx), MathF.Abs(dy)));
        }

        return maxCheb;
    }

    /// <summary>跟随目标；传 null 则自由视角（此时中心 = 平移量）。</summary>
    public void Follow(float? x, float? y)
    {
        if (x is null || y is null)
        {
            _following = false;
            return;
        }
        // 首次进入跟随或重新跟随时直接对准，避免从远处滑过来
        if (!_following)
        {
            _smoothX = x.Value;
            _smoothY = y.Value;
        }
        _following = true;
        _followX = x.Value;
        _followY = y.Value;
    }

    /// <summary>每帧推进跟随平滑（帧率无关的指数衰减，时间常数 ~40ms，降低视觉滞后）。</summary>
    public void Tick(float dtMs)
    {
        if (!_following) return;
        var k = 1 - MathF.Exp(-dtMs / 40);
        _smoothX += (_followX - _smoothX) * k;
        _smoothY += (_followY - _smoothY) * k;
    }

    public void SetZoom(float level) => _zoom = Clamp(level, _min, MathF.Max(_max, _min));

    /// <summary>以屏幕中心为锚点缩放（factor &gt; 1 放大）。</summary>
    public void ZoomBy(float factor) => SetZoom(_zoom * factor);

    /// <summary>拖拽平移（入参为屏幕像素增量）。</summary>
    public void PanBy(float screenDx, float screenDy)
    {
        var z = _zoom;
        var a = screenDx / (20 * z);
        var b = screenDy / (10 * z);
        _panX += (-a - b) / 2;
        _panY += (a - b) / 2;
    }

    /// <summary>调试/演示：把相机瞬移到世界坐标（自由视角 + 跟随时叠加平移）。</summary>
    public void Teleport(float x, float y)
    {
        _panX = x;
        _panY = y;
        _smoothX = x;
        _smoothY = y;
    }

    public float CenterX() => _following ? _smoothX + _panX : _panX;
    public float CenterY() => _following ? _smoothY + _panY : _panY;

    /// <summary>
    /// 菱形投影：世界坐标 → 屏幕。
    /// 高度项相对相机中心高度（与实体图层变换一致），否则相机中心高度变化时屏幕层会漂移。
    /// </summary>
    public Vector2 WorldToScreen(float wx, float wy, float viewW, float viewH, float? height = null)
    {
        var h = height ??
                (HeightAt is not null
                    ? HeightAt(wx, wy) - HeightAt(CenterX(), CenterY())
                    : 0);
        var z = _zoom;
        return new Vector2(
            viewW / 2 + ((wx - CenterX()) - (wy - CenterY())) * 20 * z,
            viewH / 2 + ((wx - CenterX()) + (wy - CenterY())) * 10 * z - h * 20 * z);
    }

    /// <summary>菱形投影逆变换（用地面高度迭代修正一次，点选/拾取精度足够）。</summary>
    public Vector2 ScreenToWorld(float sx, float sy, float viewW, float viewH)
    {
        var z = _zoom;
        var hCam = HeightAt is not null ? HeightAt(CenterX(), CenterY()) : 0;
        var h = 0f;
        for (var i = 0; i < 2; i++)
        {
            var a = (sx - viewW / 2) / (20 * z);
            var b = (sy - viewH / 2 + (h - hCam) * 20 * z) / (10 * z);
            var wx = CenterX() + (a + b) / 2;
            var wy = CenterY() + (b - a) / 2;
            if (i == 0 && HeightAt is not null)
            {
                h = HeightAt(wx, wy);
            }
            else
            {
                return new Vector2(wx, wy);
            }
        }
        return Vector2.Zero;
    }

    private void ClampPan(float viewW, float viewH)
    {
        if (_viewRadiusMax < 0)
        {
            return;
        }

        var half = ViewportChebyshev(viewW, viewH, _zoom);
        var budget = _viewRadiusMax - half;
        if (budget <= 0)
        {
            _panX = 0;
            _panY = 0;
            return;
        }

        var cheb = MathF.Max(MathF.Abs(_panX), MathF.Abs(_panY));
        if (cheb > budget)
        {
            var scale = budget / cheb;
            _panX *= scale;
            _panY *= scale;
        }
    }

    private static float Clamp(float v, float lo, float hi) => MathF.Max(lo, MathF.Min(hi, v));
}
