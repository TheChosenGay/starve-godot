using System;
using System.Numerics;

namespace Starve.Core;

/// <summary>
/// 实体位置平滑：服务端位置跳变时，显示位置在 durationMs 内缓动逼近。
/// 纯逻辑，时间由外部注入（渲染帧 / 测试时钟）。
/// </summary>
public sealed class PositionSmoother
{
    private readonly float _durationMs;
    private readonly float _snapDistance;
    private float _serverX;
    private float _serverY;
    private float _fromX;
    private float _fromY;
    private float _toX;
    private float _toY;
    private long _startedAt;
    private bool _has;

    public PositionSmoother(float durationMs = 160, float snapDistance = 12)
    {
        _durationMs = durationMs;
        _snapDistance = snapDistance;
    }

    /// <summary>喂服务端最新位置；now 由调用方注入（毫秒）。</summary>
    public void Update(float x, float y, long now)
    {
        if (!_has)
        {
            _serverX = _fromX = _toX = x;
            _serverY = _fromY = _toY = y;
            _startedAt = now;
            _has = true;
            return;
        }

        var dx = x - _serverX;
        var dy = y - _serverY;
        _serverX = x;
        _serverY = y;
        var dist = MathF.Sqrt(dx * dx + dy * dy);
        if (dist >= _snapDistance)
        {
            // 瞬移/传送：直接贴到目标，不做滑动
            _fromX = _toX = x;
            _fromY = _toY = y;
            _startedAt = now;
            return;
        }
        if (dist <= 0.0001f) return;

        var cur = Current(now);
        _fromX = cur.X;
        _fromY = cur.Y;
        _toX = x;
        _toY = y;
        _startedAt = now;
    }

    /// <summary>取当前显示位置。</summary>
    public Vector2 Current(long now)
    {
        if (!_has) return Vector2.Zero;
        var t = MathF.Min(1, (now - _startedAt) / _durationMs);
        var e = 1 - (1 - t) * (1 - t); // ease-out
        return new Vector2(
            _fromX + (_toX - _fromX) * e,
            _fromY + (_toY - _fromY) * e);
    }
}
