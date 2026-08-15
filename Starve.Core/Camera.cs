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
    private float _zoom = 1;
    private bool _following;
    private float _followX;
    private float _followY;
    private float _smoothX;
    private float _smoothY;
    private float _panX;
    private float _panY;
    private readonly float _base;
    private readonly float _min;
    private readonly float _max;

    /// <summary>地面高度查询（菱形投影下 screenY 减去高度×step，由地形注入）。</summary>
    public Func<float, float, float>? HeightAt { get; set; }

    public Camera(float baseScale = 40, float minZoom = 0.4f, float maxZoom = 3f)
    {
        _base = baseScale;
        _min = minZoom;
        _max = maxZoom;
    }

    /// <summary>当前世界→屏幕比例（baseScale × zoom）。</summary>
    public float Scale => _base * _zoom;

    public float ZoomLevel => _zoom;

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

    /// <summary>每帧推进跟随平滑（帧率无关的指数衰减，时间常数 ~90ms）。</summary>
    public void Tick(float dtMs)
    {
        if (!_following) return;
        var k = 1 - MathF.Exp(-dtMs / 90);
        _smoothX += (_followX - _smoothX) * k;
        _smoothY += (_followY - _smoothY) * k;
    }

    public void SetZoom(float level) => _zoom = Clamp(level, _min, _max);

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

    private static float Clamp(float v, float lo, float hi) => MathF.Max(lo, MathF.Min(hi, v));
}
