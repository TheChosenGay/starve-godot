using System;

namespace Starve.Core;

public readonly record struct MovementDiagnostics(
    float LastReconciliationError,
    float MaxReconciliationError,
    int SoftCorrections,
    int HardSnaps);

/// <summary>
/// 自己的本地移动预测：按下立即移动（不等服务端 50ms tick + 快照往返），
/// 前进时用客户端阻挡网格做墙停（避免走进树/建筑后被服务端拉回），
/// 服务端快照到达时做误差校正（大误差瞬移、小误差缓合）。
/// 纯逻辑，时间由外部注入（渲染帧 dt）。
/// </summary>
public sealed class OwnMovementSim
{
    /// <summary>默认速度：与服务端一致 10 格/秒（快照 Moveable.speed 会覆盖）。</summary>
    public const float DefaultTilesPerSec = 10f;

    private readonly Func<int, int, bool> _walkable;
    private int _anchorX;
    private int _anchorY;
    private float _subX;
    private float _subY;
    private int _dirX;
    private int _dirY;
    private float _speed = DefaultTilesPerSec;
    private bool _has;
    private float _lastReconciliationError;
    private float _maxReconciliationError;
    private int _softCorrections;
    private int _hardSnaps;

    public OwnMovementSim(Func<int, int, bool> walkable) => _walkable = walkable;

    public bool Has => _has;
    public bool Moving => _dirX != 0 || _dirY != 0;
    public (float X, float Y) Position => (_anchorX + _subX, _anchorY + _subY);
    public MovementDiagnostics Diagnostics => new(
        _lastReconciliationError,
        _maxReconciliationError,
        _softCorrections,
        _hardSnaps);

    /// <summary>出生/瞬移/大误差：直接贴合服务端位置。</summary>
    public void SnapTo(float x, float y)
    {
        SetRealPosition(x, y);
        _has = true;
    }

    /// <summary>输入方向（0,0 = 停）；与发送给服务端的命令一致。</summary>
    public void SetIntent(int dx, int dy)
    {
        _dirX = dx;
        _dirY = dy;
    }

    /// <summary>同步服务端实际速度（快照 Moveable.speed；0 = 用默认）。</summary>
    public void SetSpeed(float tilesPerSec)
    {
        if (tilesPerSec > 0) _speed = tilesPerSec;
    }

    /// <summary>
    /// 推进一帧，与服务端 MoveSystem 同公式：
    /// 位移 = speed×dt，对角归一化（÷√2），跨格校验可走、不可走贴墙停在边界，
    /// 停止时子格对齐整格。公式一致 ⇒ 服务端校正误差趋近 0，不再有“拉一下”。
    /// </summary>
    public void Tick(float dtMs)
    {
        if (!_has || dtMs <= 0) return;
        if (_dirX == 0 && _dirY == 0)
        {
            // 停止：保持当前位置，不本地快照——服务端确认停止（Dir=0, sub=0）后由
            // Reconcile 落定到它的锚点格。本地 floor 会和服务端锚点差 1 格（边界情形）。
            return;
        }
        // 渲染帧可能卡顿超过一个服务端 tick；按 50ms 分片推进，避免一次跨越多个格子。
        var remainingMs = dtMs;
        while (remainingMs > 0)
        {
            var sliceMs = MathF.Min(remainingMs, 50f);
            var dist = _speed * sliceMs / 1000f;
            if (_dirX != 0 && _dirY != 0)
            {
                dist /= MathF.Sqrt(2f); // 对角归一化：任意方向同速
            }
            if (_dirX != 0)
            {
                (_anchorX, _subX) = StepAxis(
                    _anchorX,
                    _subX,
                    _dirX,
                    dist,
                    x => _walkable(x, _anchorY));
            }
            if (_dirY != 0)
            {
                (_anchorY, _subY) = StepAxis(
                    _anchorY,
                    _subY,
                    _dirY,
                    dist,
                    y => _walkable(_anchorX, y));
            }
            remainingMs -= sliceMs;
        }
    }

    /// <summary>
    /// 与服务端 systems.stepAxis 完全相同的锚点/子格推进。
    /// 子格始终保持 [0,1)，负方向跨 0 时向锚点借位。
    /// </summary>
    private static (int Anchor, float Sub) StepAxis(
        int anchor,
        float sub,
        int dir,
        float dist,
        Func<int, bool> canWalk)
    {
        if ((sub <= 0.002f && dir < 0 && !canWalk(anchor - 1)) ||
            (sub >= 0.998f && dir > 0 && !canWalk(anchor + 1)))
        {
            return (anchor, sub);
        }

        var next = sub + dir * dist;
        if (next >= 0 && next < 1)
        {
            return (anchor, next);
        }

        var nextAnchor = anchor + dir;
        if (!canWalk(nextAnchor))
        {
            return (anchor, dir > 0 ? 0.999f : 0.001f);
        }

        if (next < 0)
        {
            return (nextAnchor, next + 1);
        }

        return (nextAnchor, next - 1);
    }

    private void SetRealPosition(float x, float y)
    {
        _anchorX = (int)MathF.Floor(x);
        _anchorY = (int)MathF.Floor(y);
        _subX = x - _anchorX;
        _subY = y - _anchorY;
        if (_subX < 0)
        {
            _anchorX--;
            _subX += 1;
        }
        if (_subY < 0)
        {
            _anchorY--;
            _subY += 1;
        }
    }

    private void BlendTo(float x, float y, float factor)
    {
        var current = Position;
        SetRealPosition(
            current.X + (x - current.X) * factor,
            current.Y + (y - current.Y) * factor);
    }

    /// <summary>
    /// 服务端快照校正：只有真正的瞬移/传送（>4 格）才硬跳。
    /// serverStopped = 服务端已确认停止（Moveable.Dir=0，sub 归零，serverX/Y 即最终锚点格）。
    /// 停止确认后才落定，且带死区：小误差（预测领先 ≤0.15 格）不往回拽，避免“停下被拉一下”。
    /// </summary>
    public void Reconcile(float serverX, float serverY, bool serverStopped = false)
    {
        if (!_has)
        {
            SnapTo(serverX, serverY);
            return;
        }
        var current = Position;
        var ex = serverX - current.X;
        var ey = serverY - current.Y;
        var err = MathF.Sqrt(ex * ex + ey * ey);
        _lastReconciliationError = err;
        _maxReconciliationError = MathF.Max(_maxReconciliationError, err);
        if (err > 4f)
        {
            _hardSnaps++;
            SnapTo(serverX, serverY);
            return;
        }
        var k = 0f;
        if (serverStopped)
        {
            // 停止落定：死区 0.15 格，超过后快速合拢（最快 50%/快照 ≈ 100ms 内到位），
            // 小误差不再慢慢拽（那是“停下回拉”的来源）。
            k = err > 0.15f ? MathF.Min(0.5f, 0.25f + err * 0.18f) : 0f;
        }
        else if (err > 0.75f)
        {
            k = 0.2f;
        }
        if (k <= 0) return;
        _softCorrections++;
        BlendTo(serverX, serverY, k);
    }
}
