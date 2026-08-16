using System;
using System.Collections.Generic;
using System.Numerics;

namespace Starve.Core;

/// <summary>
/// 实体位置平滑（延迟插值）：服务端 tick 制移动，位置每 N tick 才跳一格，
/// 单纯“追最新位置”会把 100ms 步进变成 60ms 补间 + 40ms 停顿的卡顿。
/// 本实现改为：只保留位置变化点，固定延迟 delayTicks 后做相邻两帧线性插值，
/// 让 100ms 的格步变成连续匀速运动；停止时按末段速度外推最多 maxExtrapTicks
/// 再拉平（带一点点惯性，不产生 40ms 冻结）。
/// 纯逻辑，时间用服务端 tick（DayCycle.phase），由外部注入。
/// </summary>
public sealed class PositionSmoother
{
    private readonly List<(long Tick, float X, float Y)> _samples = new();
    private readonly float _delayTicks;
    private readonly float _maxExtrapTicks;
    private readonly float _snapDistance;
    private long _latestTick;
    private long _lastUpdateWall;
    private bool _has;

    /// <param name="delayTicks">渲染延迟（tick）：2 = 100ms（覆盖 100ms 格步）。</param>
    /// <param name="maxExtrapTicks">超出最后样本时的外推上限（tick），1 = 50ms。</param>
    /// <param name="snapDistance">超过该距离视为瞬移，直接贴目标。</param>
    public PositionSmoother(float delayTicks = 2, float maxExtrapTicks = 1, float snapDistance = 12)
    {
        _delayTicks = delayTicks;
        _maxExtrapTicks = maxExtrapTicks;
        _snapDistance = snapDistance;
    }

    /// <summary>喂服务端最新位置；serverTick 为快照携带的世界 tick，wallNow 为客户端时钟（ms）。</summary>
    public void Update(float x, float y, long serverTick, long wallNow)
    {
        _latestTick = serverTick;
        _lastUpdateWall = wallNow;
        if (!_has)
        {
            _samples.Add((serverTick, x, y));
            _has = true;
            return;
        }

        var last = _samples[^1];
        var dx = x - last.X;
        var dy = y - last.Y;
        var dist = MathF.Sqrt(dx * dx + dy * dy);
        if (dist >= _snapDistance)
        {
            // 瞬移/传送：清历史直接贴目标
            _samples.Clear();
            _samples.Add((serverTick, x, y));
            return;
        }
        if (dist > 0.0001f)
        {
            // 位置变化才存样本；没变 = 停在那，靠尾部判断拉平
            _samples.Add((serverTick, x, y));
        }

        var cutoff = serverTick - (long)(_delayTicks + _maxExtrapTicks + 4);
        while (_samples.Count > 2 && _samples[0].Tick < cutoff)
        {
            _samples.RemoveAt(0);
        }
    }

    /// <summary>取当前显示位置：虚拟 tick 随墙钟连续推进，帧间也平滑，不再 20Hz 跳变。</summary>
    public Vector2 Current(long now)
    {
        if (!_has || _samples.Count == 0) return Vector2.Zero;
        if (_samples.Count == 1) return new Vector2(_samples[0].X, _samples[0].Y);

        // 距离上次快照经过的墙钟（ms）按 20Hz 折算成 tick，让插值点帧间连续前进
        var sinceUpdate = Math.Max(0, now - _lastUpdateWall);
        var dt = _latestTick + sinceUpdate / 50f - _delayTicks;
        for (var i = 1; i < _samples.Count; i++)
        {
            if (_samples[i].Tick < dt) continue;
            var a = _samples[i - 1];
            var b = _samples[i];
            var span = b.Tick - a.Tick;
            if (span <= 0) return new Vector2(b.X, b.Y);
            var t = MathF.Min(1, MathF.Max(0, (dt - a.Tick) / (float)span));
            return new Vector2(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        }

        // dt 已超过最后样本：若距上次移动还不足一个完整步进间隔（可能下一格还没到），
        // 按末段速度外推一点；否则已停，直接拉平在最后位置。
        var last = _samples[^1];
        var prev = _samples.Count >= 2 ? _samples[^2] : last;
        var beyond = dt - last.Tick;
        if (_latestTick - last.Tick >= 2 || beyond <= 0 || beyond > _maxExtrapTicks)
        {
            return new Vector2(last.X, last.Y);
        }
        var s = last.Tick - prev.Tick;
        var vx = s <= 0 ? 0 : (last.X - prev.X) / (float)s;
        var vy = s <= 0 ? 0 : (last.Y - prev.Y) / (float)s;
        return new Vector2(last.X + vx * beyond, last.Y + vy * beyond);
    }
}
