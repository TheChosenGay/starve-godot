using System;
using System.Collections.Generic;

namespace Starve.Core;

public readonly record struct MovementDiagnostics(
    float LastReconciliationError,
    float MaxReconciliationError,
    int SoftCorrections,
    int HardSnaps);

/// <summary>
/// 自己的本地移动预测：按下立即移动（不等服务端 50ms tick + 快照往返）。
/// 预测顺序与服务端 systems.MoveBody 完全一致：形状层（扫掠 + 沿接触切面滑动）→
/// 格子层（地形水/悬崖跨格校验，不可走贴边停）。占位物（树/岩/建筑）不写阻挡网格——
/// 它们只提供形状，所以本地预测要拿到 <see cref="SetBlockers"/> 的形状列表。
/// 服务端快照到达时做误差校正（大误差瞬移、小误差缓合）。纯逻辑，时间由外部注入。
/// </summary>
public sealed class OwnMovementSim
{
    /// <summary>默认速度：与服务端一致 10 格/秒（快照 Moveable.speed 会覆盖）。</summary>
    public const float DefaultTilesPerSec = 10f;

    /// <summary>缺省移动体碰撞半径（格）：与服务端 systems.BodyRadius 一致（= configs/models.json 里 player 推导值）。</summary>
    public const float DefaultBodyRadius = 0.305f;
    private float _bodyRadius = DefaultBodyRadius;

    private readonly Func<int, int, bool> _walkable;
    private IReadOnlyList<BlockerShape> _blockers = Array.Empty<BlockerShape>();
    private IReadOnlyList<OrcaNeighbor> _neighbors = Array.Empty<OrcaNeighbor>();
    // 对称打破的 key = 自己的实体 id（与服务端一致：按 id 奇偶决定往哪侧让）。
    // 0 是占位，SetSelfKey 会在登录拿到 entity id 后设置。
    private OrcaAvoidance _orca = new(OrcaOptions.Default, true, 0u);
    private float _velX, _velY;
    private float _bodyHalfLength;
    private float _effectiveSpeed;
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
    // 已对哪一 tick 的服务端位置收敛过（-1 = 还没收敛过）；用于丢弃陈旧快照。
    private long _lastReconciledTick = -1;
    // 停止是否已经落定：落定之后同 tick/更旧的旧快照不再参与校正。
    private bool _stopSettled;

    public Func<float, float, float>? HeightAt { get; set; }

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
        // 出生/瞬移/硬跳后，之前记录的新鲜度基线作废：下一份快照应被当作新的。
        _lastReconciledTick = -1;
        _stopSettled = false;
    }

    /// <summary>输入方向（0,0 = 停）；与发送给服务端的命令一致。</summary>
    public void SetIntent(int dx, int dy)
    {
        _dirX = dx;
        _dirY = dy;
    }

    /// <summary>同步服务端效果修正后的权威速度；0 表示被冻结。</summary>
    public void SetSpeed(float tilesPerSec)
    {
        if (tilesPerSec >= 0) _speed = tilesPerSec;
    }

    /// <summary>
    /// 同步服务端下发的实体碰撞半径（Moveable.body_radius，由客户端模型推导）。
    /// 半径不一致就会贴着树/墙来回校正，所以必须用服务端的值，不要用客户端常量。
    /// </summary>
    public void SetBodyRadius(float radius)
    {
        if (radius > 0f) _bodyRadius = radius;
    }

    /// <summary>
    /// 同步占位物形状（快照里的 Block：圆=格心圆，盒=占格矩形）。
    /// 形状变了就调一次；空列表 = 本帧没有已知占位物。
    /// </summary>
    public void SetBlockers(IReadOnlyList<BlockerShape> blockers) =>
        _blockers = blockers ?? Array.Empty<BlockerShape>();

    /// <summary>
    /// 动态邻居（ORCA 用）：位置/速度/半径。由外部每帧从快照刷新。
    /// 只放**其他**动态实体（玩家/动物），不放静态障碍——静态走硬碰撞（阶段②）。
    /// </summary>
    public void SetNeighbors(IReadOnlyList<OrcaNeighbor> neighbors) =>
        _neighbors = neighbors ?? Array.Empty<OrcaNeighbor>();

    /// <summary>本 tick 的实际速度（格/秒）：ORCA 的"当前速度"输入，也是表现层依据。</summary>
    public (float X, float Y) Velocity => (_velX, _velY);

    /// <summary>设置自己的实体 id（ORCA 对称打破用；须与服务端下发的 id 一致）。</summary>
    public void SetSelfKey(ulong entityId) =>
        _orca = new OrcaAvoidance(OrcaOptions.Default, true, entityId);

    /// <summary>同步服务端下发的有效速度与胶囊半长（ORCA 的输入维度）。</summary>
    public void SetSpeedProfile(float effectiveSpeed, float halfLength)
    {
        _effectiveSpeed = effectiveSpeed;
        _bodyHalfLength = halfLength;
    }

    /// <summary>
    /// 推进一帧，与服务端三阶段移动同公式：
    /// ① desired：位移 = effective_speed × 坡度因子 × dt，对角归一化（÷√2）；
    /// ② slide  ：对**静态**障碍扫掠 + 沿接触切面投影（硬约束，绝不穿模）；
    /// ③ avoid  ：ORCA 对其他动态体避让，产出方向任意的最终速度；
    /// 最后走格子层（跨格校验地形可走）。
    ///
    /// 这三步的顺序与服务端 systems/move_solver.go 严格一致；ORCA 也是同一套公式
    /// （Starve.Core/OrcaAvoidance.cs 是 internal/game/systems/orca.go 的移植）。
    /// </summary>
    public void Tick(float dtMs)
    {
        if (!_has || dtMs <= 0) return;
        if (_dirX == 0 && _dirY == 0)
        {
            // 连续移动允许停在任意子格；服务端同样保留 sub，不吸附整数格。
            return;
        }
        // 渲染帧可能卡顿超过一个服务端 tick；按 50ms 分片推进，避免一次跨越多个格子。
        var remainingMs = dtMs;
        while (remainingMs > 0)
        {
            var sliceMs = MathF.Min(remainingMs, 50f);
            var pos = Position;
            var factor = SlopeSpeed.Factor(pos.X, pos.Y, _dirX, _dirY, HeightAt);
            var dist = _speed * factor * sliceMs / 1000f;
            if (_dirX != 0 && _dirY != 0)
            {
                dist /= MathF.Sqrt(2f); // 对角归一化：任意方向同速
            }

            var stepX = _dirX * dist;
            var stepY = _dirY * dist;
            if (_blockers.Count > 0)
            {
                // 形状层：滑动后的实际位移，方向可能已经变了
                var (endX, endY) = MovementSlide.Slide(pos.X, pos.Y, stepX, stepY, _bodyRadius, _blockers);
                stepX = endX - pos.X;
                stepY = endY - pos.Y;
            }
            // ── 阶段③：ORCA 动态避让（软约束）─────────────────
            // 把阶段②的结果折算成期望速度，再用 ORCA 求一个既接近它、又不撞邻居的速度。
            if (_neighbors.Count > 0)
            {
                var sliceSec = sliceMs / 1000f;
                var prefVX = stepX / sliceSec;
                var prefVY = stepY / sliceSec;
                var selfVX = _velX != 0f || _velY != 0f ? _velX : prefVX;
                var selfVY = _velX != 0f || _velY != 0f ? _velY : prefVY;

                var agent = new OrcaAgent
                {
                    X = pos.X, Z = pos.Y,
                    VX = selfVX, VY = selfVY,
                    PrefVX = prefVX, PrefVY = prefVY,
                    // 胶囊按外接圆处理（与服务端 collectNeighbors 的半径算法一致）
                    Radius = _bodyRadius + _bodyHalfLength,
                    MaxSpeed = _effectiveSpeed > 0 ? _effectiveSpeed : _speed,
                };
                var bodies = new List<OrcaBody>(_neighbors.Count);
                foreach (var n in _neighbors)
                    bodies.Add(new OrcaBody
                    {
                        X = n.X, Z = n.Y, VX = n.VX, VY = n.VY,
                        Radius = n.Radius + n.HalfLength,
                        MaxSpeed = n.MaxSpeed,
                    });
                _orca.Solve(agent, bodies, out var avx, out var avy);
                _velX = avx;
                _velY = avy;
                stepX = avx * sliceSec;
                stepY = avy * sliceSec;
            }
            else
            {
                _velX = sliceMs > 0 ? stepX / (sliceMs / 1000f) : 0f;
                _velY = sliceMs > 0 ? stepY / (sliceMs / 1000f) : 0f;
            }

            if (stepX != 0f)
            {
                (_anchorX, _subX) = StepAxis(
                    _anchorX,
                    _subX,
                    MathF.Sign(stepX),
                    MathF.Abs(stepX),
                    x => _walkable(x, _anchorY));
            }
            if (stepY != 0f)
            {
                (_anchorY, _subY) = StepAxis(
                    _anchorY,
                    _subY,
                    MathF.Sign(stepY),
                    MathF.Abs(stepY),
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
    /// serverStopped = 服务端已确认停止（Moveable.Dir=0 且路径为空）。
    /// serverTick = 这份位置是哪一 tick 下发的（-1 = 未知，按"新鲜"处理，兼容旧调用方）。
    ///
    /// 陈旧判定（修"停下抖动"）：增量快照只带脏组件，玩家停下后服务端不再标脏
    /// Position/Moveable，这两个组件会一直停留在停下那一刻的值，而每次增量仍会让
    /// Revision +1、仍会调用本函数。如果反复拿这份冻结值收敛，就会"停下后被回拉"，
    /// 下次移动时又被往前拽——即抖动。所以：
    ///   - 已经对某个 tick 的位置收敛过停止 → 同一 tick（更旧）再调用直接忽略；
    ///   - 位置比上次收敛的更旧 → 同样忽略。
    /// 这样停止只收敛一次，之后完全不干预本地预测。
    /// </summary>
    public void Reconcile(float serverX, float serverY, bool serverStopped = false, long serverTick = -1)
    {
        if (!_has)
        {
            SnapTo(serverX, serverY);
            _lastReconciledTick = serverTick;
            _stopSettled = serverStopped;
            return;
        }
        // 陈旧数据：这份位置不包含新信息，收敛它只会把角色往回拉。
        var stale = serverTick >= 0 && _lastReconciledTick >= 0 && serverTick <= _lastReconciledTick;
        if (stale)
        {
            if (serverStopped && _stopSettled) return; // 停止已落定，旧快照一律不理会
            if (!serverStopped) return;               // 移动中的旧位置更没有收敛价值
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
            _lastReconciledTick = serverTick;
            _stopSettled = serverStopped;
            return;
        }
        var k = 0f;
        if (serverStopped)
        {
            // 停止落定：死区 0.15 格，超过后快速合拢（最快 50%/快照 ≈ 100ms 内到位），
            // 小误差不再慢慢拽（那是"停下回拉"的来源）。
            k = err > 0.15f ? MathF.Min(0.5f, 0.25f + err * 0.18f) : 0f;
        }
        else if (err > 0.75f)
        {
            k = 0.2f;
        }
        if (k <= 0)
        {
            _lastReconciledTick = serverTick;
            _stopSettled = serverStopped;
            return;
        }
        _softCorrections++;
        // 停止落定用一次到位（k=1）而不是渐进逼近：渐进会把"回拉"摊成好几次
        // 20Hz 跳变，正是看得见的抖动。移动中仍保留小步收敛，避免突兀。
        BlendTo(serverX, serverY, serverStopped ? 1f : k);
        _lastReconciledTick = serverTick;
        _stopSettled = serverStopped;
    }
}
