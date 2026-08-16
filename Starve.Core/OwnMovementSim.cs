using System;

namespace Starve.Core;

/// <summary>
/// 自己的本地移动预测：按下立即移动（不等服务端 100ms 格步 + 快照往返），
/// 前进时用客户端阻挡网格做墙停（避免走进树/建筑后被服务端拉回），
/// 服务端快照到达时做误差校正（大误差瞬移、小误差缓合）。
/// 纯逻辑，时间由外部注入（渲染帧 dt）。
/// </summary>
public sealed class OwnMovementSim
{
    /// <summary>与服务端一致：10 格/秒（100ms 一格）。</summary>
    public const float TilesPerMs = 0.01f;

    private readonly Func<int, int, bool> _walkable;
    private float _x;
    private float _y;
    private int _dirX;
    private int _dirY;
    private bool _has;

    public OwnMovementSim(Func<int, int, bool> walkable) => _walkable = walkable;

    public bool Has => _has;
    public bool Moving => _dirX != 0 || _dirY != 0;
    public (float X, float Y) Position => (_x, _y);

    /// <summary>出生/瞬移/大误差：直接贴合服务端位置。</summary>
    public void SnapTo(float x, float y)
    {
        _x = x;
        _y = y;
        _has = true;
    }

    /// <summary>输入方向（0,0 = 停）；与发送给服务端的命令一致。</summary>
    public void SetIntent(int dx, int dy)
    {
        _dirX = dx;
        _dirY = dy;
    }

    /// <summary>推进一帧；子步进避免穿过障碍格（与服务端逐格丢弃不可走步一致）。</summary>
    public void Tick(float dtMs)
    {
        if (!_has || dtMs <= 0) return;
        var total = dtMs * TilesPerMs;
        if (total <= 0) return;
        var steps = Math.Max(1, (int)MathF.Ceiling(total / 0.2f));
        var step = total / steps;
        for (var i = 0; i < steps; i++)
        {
            if (_dirX != 0)
            {
                var nx = _x + _dirX * step;
                if ((int)MathF.Floor(nx) != (int)MathF.Floor(_x) &&
                    !_walkable((int)MathF.Floor(nx), (int)MathF.Floor(_y)))
                {
                    _x = _dirX > 0 ? MathF.Floor(_x) + 1 : MathF.Floor(_x);
                    _dirX = 0; // 撞墙停住，等服务端确认（不再继续推）
                }
                else
                {
                    _x = nx;
                }
            }
            if (_dirY != 0)
            {
                var ny = _y + _dirY * step;
                if ((int)MathF.Floor(ny) != (int)MathF.Floor(_y) &&
                    !_walkable((int)MathF.Floor(_x), (int)MathF.Floor(ny)))
                {
                    _y = _dirY > 0 ? MathF.Floor(_y) + 1 : MathF.Floor(_y);
                    _dirY = 0;
                }
                else
                {
                    _y = ny;
                }
            }
        }
    }

    /// <summary>服务端快照校正：大误差瞬移，小误差按比例缓合。</summary>
    public void Reconcile(float serverX, float serverY)
    {
        if (!_has)
        {
            SnapTo(serverX, serverY);
            return;
        }
        var ex = serverX - _x;
        var ey = serverY - _y;
        var err = MathF.Sqrt(ex * ex + ey * ey);
        if (err > 1.5f)
        {
            SnapTo(serverX, serverY);
            return;
        }
        // 移动中服务端一般落后（tick 对齐差 ≤1 格），只对“服务端在我们前面”
        // 或已停止的误差做缓合，避免把正常的预测领先也拉回去。
        var k = 0f;
        if (!Moving) k = err > 0.02f ? 0.15f : 0f;
        else if (err > 0.45f) k = 0.3f;
        if (k <= 0) return;
        _x += ex * k;
        _y += ey * k;
    }
}
