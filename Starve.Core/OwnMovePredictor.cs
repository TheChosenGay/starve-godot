using Starve.Netcode;

namespace Starve.Core;

/// <summary>
/// 本端移动预测的**单步推进**（与服务端 <c>MoveSolver</c> + <c>ApplyDisplacement</c> 同构，
/// 由 <c>movement_golden.json</c> 守契约）。
///
/// 它是 <see cref="INetModel{TState,TAction}"/> 的实现：**只有世界视图 + 配置，没有链状态**。
/// 所有跨步状态都在 <see cref="OwnMoveState"/> 里 —— 这样"玩家移动"和"组件内部重放"
/// 可以调用同一个实例，结果一致。用 <c>ModelContract.AssertContract</c> 校验。
///
/// 世界视图（可走性、静态障碍、动态邻居、地形、速度档位）由调用方**整体替换**，
/// <see cref="Step"/> 内部不演化它们、也不随调用次数累积。
/// </summary>
public sealed class OwnMovePredictor : INetModel<OwnMoveState, MoveIntent>
{
    private readonly Func<int, int, bool> _walkable;
    private IReadOnlyList<BlockerShape> _blockers = Array.Empty<BlockerShape>();
    private IReadOnlyList<OrcaNeighbor> _neighbors = Array.Empty<OrcaNeighbor>();
    // 对称打破是世界系常量（见 OrcaAvoidance.SideBias），不需要实体 id。
    private readonly OrcaAvoidance _orca = new(OrcaOptions.Default);

    public OwnMovePredictor(Func<int, int, bool> walkable) =>
        _walkable = walkable ?? throw new ArgumentNullException(nameof(walkable));

    /// <summary>地面高度查询（移动/坡度用**逻辑高度**：始终崖壁带，与服务端一致）。</summary>
    public Func<float, float, float>? HeightAt { get; set; }

    public float BodyRadius { get; private set; } = OwnMovementSim.DefaultBodyRadius;

    public float BodyHalfLength { get; private set; }

    /// <summary>基础速度（格/秒）。</summary>
    public float Speed { get; private set; } = OwnMovementSim.DefaultTilesPerSec;

    /// <summary>效果修正后的权威速度（服务端 <c>effective_speed</c>）。</summary>
    public float EffectiveSpeed { get; private set; }

    /// <summary>最近一步用的坡度因子（诊断/日志）。</summary>
    public float LastSlope { get; private set; } = 1f;

    // ── 世界视图注入（不得在 Step 内演化）───────────────────────
    public void SetBlockers(IReadOnlyList<BlockerShape> blockers) =>
        _blockers = blockers ?? Array.Empty<BlockerShape>();

    public void SetNeighbors(IReadOnlyList<OrcaNeighbor> neighbors) =>
        _neighbors = neighbors ?? Array.Empty<OrcaNeighbor>();

    public void SetBodyRadius(float radius)
    {
        if (radius > 0f) BodyRadius = radius;
    }

    public void SetSpeed(float tilesPerSec)
    {
        if (tilesPerSec >= 0f) Speed = tilesPerSec;
    }

    public void SetSpeedProfile(float effectiveSpeed, float halfLength)
    {
        EffectiveSpeed = effectiveSpeed;
        BodyHalfLength = halfLength;
    }

    /// <summary>自己的实体 id（ORCA 对称打破用；须与服务端一致）。</summary>

    /// <summary>
    /// 推进一步。dt 由调用方给：组件按服务端 tick 长（50ms）调用，
    /// 旧门面 <see cref="OwnMovementSim"/> 按渲染帧长分片调用 —— 两者走同一段数学。
    /// </summary>
    public uint Step(ref OwnMoveState state, in MoveIntent action, double dtSeconds, uint baseVersion)
    {
        // 无意图：连续移动允许停在任意子格；服务端同样保留 sub，不吸附整数格。
        if (action.Dx == 0 && action.Dy == 0) return baseVersion;
        var sliceMs = (float)(dtSeconds * 1000.0);
        if (sliceMs <= 0f) return baseVersion;

        var posX = state.X;
        var posY = state.Y;
        var factor = SlopeSpeed.Factor(posX, posY, action.Dx, action.Dy, HeightAt);
        LastSlope = factor;

        var dist = Speed * factor * sliceMs / 1000f;
        if (action.Dx != 0 && action.Dy != 0)
        {
            dist /= MathF.Sqrt(2f); // 对角归一化：任意方向同速
        }

        var stepX = action.Dx * dist;
        var stepY = action.Dy * dist;
        if (_blockers.Count > 0)
        {
            // 形状层：滑动后的实际位移，方向可能已经变了
            var (endX, endY) = MovementSlide.Slide(posX, posY, stepX, stepY, BodyRadius, _blockers);
            stepX = endX - posX;
            stepY = endY - posY;
        }

        // ── 阶段③：ORCA 动态避让（软约束）─────────────────────
        if (_neighbors.Count > 0)
        {
            var sliceSec = sliceMs / 1000f;
            var prefVX = stepX / sliceSec;
            var prefVY = stepY / sliceSec;
            var selfVX = state.VelX != 0f || state.VelY != 0f ? state.VelX : prefVX;
            var selfVY = state.VelX != 0f || state.VelY != 0f ? state.VelY : prefVY;

            var agent = new OrcaAgent
            {
                X = posX, Z = posY,
                VX = selfVX, VY = selfVY,
                PrefVX = prefVX, PrefVY = prefVY,
                // 胶囊按外接圆处理（与服务端 collectNeighbors 的半径算法一致）
                Radius = BodyRadius + BodyHalfLength,
                MaxSpeed = EffectiveSpeed > 0f ? EffectiveSpeed : Speed,
            };
            var bodies = new List<OrcaBody>(_neighbors.Count);
            foreach (var n in _neighbors)
            {
                bodies.Add(new OrcaBody
                {
                    X = n.X, Z = n.Y, VX = n.VX, VY = n.VY,
                    Radius = n.Radius + n.HalfLength,
                    MaxSpeed = n.MaxSpeed,
                });
            }

            _orca.Solve(agent, bodies, out var avx, out var avy);
            state.VelX = avx;
            state.VelY = avy;
            stepX = avx * sliceSec;
            stepY = avy * sliceSec;
        }
        else
        {
            state.VelX = stepX / (sliceMs / 1000f);
            state.VelY = stepY / (sliceMs / 1000f);
        }

        if (stepX != 0f)
        {
            var anchorY = state.AnchorY;
            var (anchorX, subX) = StepAxis(
                state.AnchorX, state.SubX, MathF.Sign(stepX), MathF.Abs(stepX), x => _walkable(x, anchorY));
            state.AnchorX = anchorX;
            state.SubX = subX;
        }

        if (stepY != 0f)
        {
            var anchorX = state.AnchorX;
            var (anchorY, subY) = StepAxis(
                state.AnchorY, state.SubY, MathF.Sign(stepY), MathF.Abs(stepY), y => _walkable(anchorX, y));
            state.AnchorY = anchorY;
            state.SubY = subY;
        }

        return baseVersion;
    }

    public float Distance(in OwnMoveState a, in OwnMoveState b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// 位移。返回的 <see cref="OwnMoveState"/> 是"位置即位移"的载体：
    /// 只有 <c>X</c>/<c>Y</c>（= 锚点+子格）有意义，速度不参与。
    /// </summary>
    public OwnMoveState Delta(in OwnMoveState from, in OwnMoveState to) =>
        OwnMoveState.FromContinuous(to.X - from.X, to.Y - from.Y);

    /// <summary>把位移按 k 倍加到当前位置上（残差平滑用）。k=0 原样返回。</summary>
    public OwnMoveState AddDelta(in OwnMoveState state, in OwnMoveState delta, float k)
    {
        var moved = OwnMoveState.FromContinuous(state.X + k * delta.X, state.Y + k * delta.Y);
        moved.VelX = state.VelX;
        moved.VelY = state.VelY;
        return moved;
    }

    /// <summary>
    /// 与服务端 <c>stepAxis</c> 完全相同的锚点/子格推进。
    /// 子格始终保持 [0,1)，负方向跨 0 时向锚点借位。
    /// </summary>
    private static (int Anchor, float Sub) StepAxis(
        int anchor, float sub, int dir, float dist, Func<int, bool> canWalk)
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
}
