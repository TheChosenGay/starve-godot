using System;
using System.Collections.Generic;
using System.Numerics;

namespace Starve.Core;

/// <summary>
/// 实体位置平滑（延迟插值 + 速度外推 + 残差平滑吸收）。
///
/// 背景：服务端 tick 制移动，位置以 20Hz 快照广播连续子格偏移（Position + Sub），
/// 而渲染是 60FPS 且网络有抖动。若只做插值，一旦某个样本迟到或用尽，输出就会
/// "卡住等下一帧"；若改成直接跳向最新位置，迟到样本到达时又会突跳。
///
/// 本实现分三层：
///   ① <b>延迟插值</b>：固定延迟 delayTicks 后做相邻两样本线性插值，
///      把 20Hz 样本补成 60FPS 连续运动；
///   ② <b>速度外推</b>：样本用尽（网络抖动 / 丢包 / 该 tick 未下发）时继续按速度前进。
///      速度优先取服务端权威 <c>Moveable.VelX/VelY</c>（已含坡度与避让结果，比末段位移准）；
///      服务端明确停止（速度清零）时**不再外推**；
///      外推量在超过 maxExtrapTicks 后线性收力至 0，避免到点向后瞬跳；
///   ③ <b>残差平滑吸收</b>：外推期间到达的新样本若与"上一帧实际显示位置"不符
///      （残差 &gt; blendThreshold），<b>不瞬移</b>——把残差作为衰减量叠加到输出上，
///      在 blendTicks 内平滑收敛，因此服务端数据与预测不一致时是连续过渡而非跳变。
///
/// 纯逻辑，时间用服务端 tick 与外部注入的客户端墙钟。
/// </summary>
public sealed class PositionSmoother
{
    private readonly List<Sample> _samples = new();
    private readonly float _delayTicks;
    private readonly float _maxExtrapTicks;
    private readonly float _snapDistance;
    private readonly float _blendThreshold;
    private readonly float _blendTicks;
    private long _latestTick;
    private long _lastUpdateWall;
    private bool _has;

    // 服务端权威速度（格/秒）；_hasServerVel 为 true 且为 (0,0) 表示"服务端确认停止"。
    // 此时禁止外推——否则停下后还会靠末段位移继续滑行。
    private float _serverVelX;
    private float _serverVelY;
    private bool _hasServerVel;

    // 预测脱节时的平滑收敛：把"脱节瞬间的显示位置"记为混合起点，
    // 在 blendTicks 内线性过渡到权威轨迹，等价于把一次跳变摊成一段短过渡。
    // 上一渲染帧是否处于外推状态：只有"上一帧靠猜"时，新样本与显示的差异
    // 才代表预测脱节，需要平滑过渡。正常插值跟随时不应触发。
    private bool _pendingWasExtrapolated;

    private float _blendFromX;
    private float _blendFromY;
    private long _blendStartWall;
    private long _blendUntilWall;

    private readonly struct Sample
    {
        public readonly long Tick;
        public readonly float X;
        public readonly float Y;

        public Sample(long tick, float x, float y)
        {
            Tick = tick;
            X = x;
            Y = y;
        }
    }

    /// <param name="delayTicks">渲染延迟（tick）：服务端每 tick 广播子格偏移，1 = 50ms 即可平滑。</param>
    /// <param name="maxExtrapTicks">样本用尽后的外推上限（tick），1 = 50ms。</param>
    /// <param name="snapDistance">超过该距离视为瞬移（传送），直接贴目标并清误差。</param>
    /// <param name="blendThreshold">外推残差超过该值（格）才启动平滑吸收；更小视为噪声，直接对齐。</param>
    /// <param name="blendTicks">残差平滑吸收时长（tick），4 = 200ms。</param>
    public PositionSmoother(
        float delayTicks = 1,
        float maxExtrapTicks = 1,
        float snapDistance = 12,
        float blendThreshold = 0.08f,
        float blendTicks = 4f)
    {
        _delayTicks = delayTicks;
        _maxExtrapTicks = maxExtrapTicks;
        _snapDistance = snapDistance;
        _blendThreshold = blendThreshold;
        _blendTicks = blendTicks;
    }

    /// <summary>外推时是否使用服务端权威速度（默认 true）。</summary>
    public bool UseServerVelocity { get; set; } = true;

    /// <summary>最近一次取位置时是否处于外推状态（样本已用尽），供诊断与测试。</summary>
    public bool Extrapolating { get; private set; }

    /// <summary>最近一次由残差触发的平滑过渡强度（格）；0 = 未发生过渡。</summary>
    public float LastBlendDistance { get; private set; }

    /// <summary>最近一次实际显示位置，用于判断新样本是否与预测脱节。</summary>
    private float _lastOutputX;
    private float _lastOutputY;
    private bool _hasOutput;

    /// <summary>
    /// 同步服务端权威速度（格/秒，来自 Moveable.VelX/VelY）。
    /// 服务端停止时会传 (0,0)，此时外推立即失效（不再滑行）。
    /// </summary>
    public void SetServerVelocity(float vx, float vy)
    {
        _serverVelX = vx;
        _serverVelY = vy;
        _hasServerVel = true;
    }

    /// <summary>喂服务端最新位置；serverTick 为快照携带的世界 tick，wallNow 为客户端时钟（ms）。</summary>
    public void Update(float x, float y, long serverTick, long wallNow)
    {
        _latestTick = serverTick;
        _lastUpdateWall = wallNow;
        if (!_has)
        {
            _samples.Add(new Sample(serverTick, x, y));
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
            _samples.Add(new Sample(serverTick, x, y));
            ClearBlend();
            _hasOutput = false;
            return;
        }

        // ── 记录本帧的权威目标，供 Current() 做脱节判定 ─────────────
        //
        // 注意：这里**不做**脱节判定。判定必须放在 Current() 里做，因为只有那里
        // 才同时知道"此刻显示在哪"和"权威轨迹此刻应该在哪"。
        // 早先在 Update() 里拿 _lastOutputX 与"新样本折算的延迟位置"比较，
        // 两者时间基准不一致（输出是上一帧的，期望值已按 50ms 折算过），
        // 稳态运动时残差恒为半个 tick 的位移，于是每个快照都误判为脱节、
        // 混合被反复重启 —— 表现为持续按住方向键时剧烈抖动。

        if (dist > 0.0001f)
        {
            // 位置变化才存样本；没变 = 停在那，靠尾部判断拉平
            _samples.Add(new Sample(serverTick, x, y));
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

        var p = SampleAt(now, out var extrapolating);
        Extrapolating = extrapolating;

        // ── 预测脱节的平滑收敛（在渲染帧里判定，时间基准统一）─────────
        //
        // 只有真正"被预测带偏"时才需要过渡，即：上一帧处于**外推**状态
        // （样本用尽、靠速度猜），而这一帧新样本到了、且与猜的位置不符。
        // 正常插值区间里样本本来就在轨迹上，绝不能触发过渡——
        // 之前误在 Update() 里比较，导致稳态运动时每个快照都误判、反复重启混合，
        // 输出被一再拉回旧位置，表现为持续移动时剧烈抖动。
        if (_hasOutput && _blendUntilWall <= now && _pendingWasExtrapolated)
        {
            var ex = _lastOutputX - p.X;
            var ey = _lastOutputY - p.Y;
            var errDist = MathF.Sqrt(ex * ex + ey * ey);
            if (errDist > _blendThreshold)
            {
                _blendFromX = _lastOutputX;
                _blendFromY = _lastOutputY;
                _blendStartWall = now;
                _blendUntilWall = now + (long)(_blendTicks * 50f);
                LastBlendDistance = errDist;
            }
            else
            {
                ClearBlend();
            }
        }

        var f = BlendFactor(now);
        var outX = _blendFromX + (p.X - _blendFromX) * f;
        var outY = _blendFromY + (p.Y - _blendFromY) * f;
        _lastOutputX = outX;
        _lastOutputY = outY;
        _hasOutput = true;
        // 记下"本帧是否靠外推"，供下一帧判定新样本是否与预测不符。
        _pendingWasExtrapolated = extrapolating;
        return new Vector2(outX, outY);
    }

    /// <summary>
    /// 纯插值/外推结果，**不修改任何状态**。extrapolating 表示本帧是否走了外推分支。
    /// </summary>
    private Vector2 SampleAt(long now, out bool extrapolating)
    {
        // 距离上次快照经过的墙钟（ms）按 20Hz 折算成 tick，让插值点帧间连续前进
        var sinceUpdate = Math.Max(0, now - _lastUpdateWall);
        var dt = _latestTick + sinceUpdate / 50f - _delayTicks;
        return Evaluate(dt, out extrapolating);
    }

    /// <summary>
    /// 求"虚拟 tick = targetTick 时轨迹应在的位置"。用于两处：
    ///   - <see cref="Current"/> 按当前墙钟推进；
    ///   - 残差判据里把新样本折算成期望延迟位置。
    /// 纯函数，不改状态。
    /// </summary>
    private Vector2 Evaluate(float targetTick, out bool extrapolating)
    {
        extrapolating = false;
        if (_samples.Count == 0) return Vector2.Zero;
        if (_samples.Count == 1) return new Vector2(_samples[0].X, _samples[0].Y);

        for (var i = 1; i < _samples.Count; i++)
        {
            if (_samples[i].Tick < targetTick) continue;
            var a = _samples[i - 1];
            var b = _samples[i];
            var span = b.Tick - a.Tick;
            if (span <= 0) return new Vector2(b.X, b.Y);
            var t = MathF.Min(1, MathF.Max(0, (targetTick - a.Tick) / (float)span));
            return new Vector2(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        }

        // targetTick 已超过最后样本：位置更新没跟上（网络抖动 / 该 tick 服务端未下发）。
        var last = _samples[^1];
        var prev = _samples.Count >= 2 ? _samples[^2] : last;
        var beyond = targetTick - last.Tick;
        if (beyond <= 0) return new Vector2(last.X, last.Y);

        // 服务端确认停止（权威速度清零）→ 不外推，直接停在最后位置。
        // 注：下一段用权威速度算出 (0,0) 后也会走到同样的短路分支，
        // 所以这里判的不是"唯一防线"，而是把**停止语义**显式写出来：
        // 停止时绝不回退到末段位移速度去滑行。
        if (_hasServerVel && _serverVelX == 0f && _serverVelY == 0f)
        {
            return new Vector2(last.X, last.Y);
        }

        // 权威速度优先（含坡度/避让结果，比末段位移准）；否则回退到末段位移速度。
        float vx, vy;
        if (UseServerVelocity && _hasServerVel)
        {
            vx = _serverVelX / 50f; // 格/秒 → 格/tick
            vy = _serverVelY / 50f;
        }
        else
        {
            var s = last.Tick - prev.Tick;
            vx = s <= 0 ? 0 : (last.X - prev.X) / (float)s;
            vy = s <= 0 ? 0 : (last.Y - prev.Y) / (float)s;
        }
        if (vx == 0f && vy == 0f) return new Vector2(last.X, last.Y);

        // 外推上限：到达后**冻结端点**，不再继续推进。
        //
        // 这里不能写成 vx*beyond*decay（对增长中的 beyond 做衰减）：
        // beyond 每帧都在增长，而 decay 同时在下降，两者相乘会让结果**先增后减**，
        // 于是画面在外推超限后开始**倒退**（实测倒退 0.79 格，明显橡皮筋）。
        // 正确做法是把外推距离钳在上限处，画面平滑停住、绝不后退。
        var clamped = MathF.Min(beyond, _maxExtrapTicks);
        extrapolating = true;
        return new Vector2(last.X + vx * clamped, last.Y + vy * clamped);
    }

    /// <summary>
    /// 混合系数：0 = 完全用混合起点（脱节瞬间的显示位置），1 = 完全用权威轨迹。
    /// 从 0 线性升到 1，因此过渡结束必然精确落在权威位置上。
    /// </summary>
    private float BlendFactor(long now)
    {
        if (_blendUntilWall <= now) return 1f;
        var total = _blendTicks * 50f;
        if (total <= 0f) return 1f;
        var elapsed = (float)(now - _blendStartWall);
        // 起点帧也必须至少有**一帧**的进度：elapsed=0 时返回 0 会把输出钉死在
        // 混合起点（= 脱节瞬间的旧位置），表现为每隔一个混合周期画面"卡一下"。
        // 实测该写法在 200ms 周期上稳定产生一次零位移帧。
        if (elapsed <= 0f) elapsed = 1000f / 60f;
        return MathF.Min(1f, MathF.Max(0f, elapsed / total));
    }

    private void ClearBlend()
    {
        _blendUntilWall = 0;
        _blendStartWall = 0;
        LastBlendDistance = 0f;
    }
}
