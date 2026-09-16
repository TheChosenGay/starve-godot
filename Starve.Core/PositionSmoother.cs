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

        // ── 预测脱节的平滑收敛 ───────────────────────────────────
        // 判据：**上一帧实际显示的位置** 与这份权威样本是否脱节。
        // 用实际输出（而不是重新采样）才能覆盖"外推过头"与"丢包后回拉"两种情况。
        //
        // 收敛方式：记录当前显示位置为混合起点，在 blendTicks 内从它过渡到
        // **权威轨迹**（SampleAt 的结果），而不是给基础轨迹叠加一个残差偏移。
        // 叠加残差的写法会与"基础轨迹自身仍在向新样本插值"叠加，导致先过冲
        // 再回摆，且最终停在错误位置（实测收敛到 10.912 而非权威 10.112）。
        // 起点→权威轨迹的混合是单调的，按定义必然收敛到权威位置。
        if (_hasOutput && _blendUntilWall <= wallNow)
        {
            var ex = _lastOutputX - x;
            var ey = _lastOutputY - y;
            var errDist = MathF.Sqrt(ex * ex + ey * ey);
            if (errDist > _blendThreshold)
            {
                _blendFromX = _lastOutputX;
                _blendFromY = _lastOutputY;
                _blendStartWall = wallNow;
                _blendUntilWall = wallNow + (long)(_blendTicks * 50f);
                LastBlendDistance = errDist;
            }
            else
            {
                ClearBlend();
            }
        }

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
        var f = BlendFactor(now);
        var outX = _blendFromX + (p.X - _blendFromX) * f;
        var outY = _blendFromY + (p.Y - _blendFromY) * f;
        _lastOutputX = outX;
        _lastOutputY = outY;
        _hasOutput = true;
        return new Vector2(outX, outY);
    }

    /// <summary>
    /// 纯插值/外推结果，**不修改任何状态**。extrapolating 表示本帧是否走了外推分支。
    /// </summary>
    private Vector2 SampleAt(long now, out bool extrapolating)
    {
        extrapolating = false;
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

        // dt 已超过最后样本：位置更新没跟上（网络抖动 / 该 tick 服务端未下发）。
        var last = _samples[^1];
        var prev = _samples.Count >= 2 ? _samples[^2] : last;
        var beyond = dt - last.Tick;
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
        var elapsed = now - _blendStartWall;
        return MathF.Min(1f, MathF.Max(0f, elapsed / total));
    }

    private void ClearBlend()
    {
        _blendUntilWall = 0;
        _blendStartWall = 0;
        LastBlendDistance = 0f;
    }
}
