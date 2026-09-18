namespace Starve.Netcode;

/// <summary>
/// 远端实体（<c>otherCache</c>）：只用服务端真实数据，**不预测**。
///
/// 做法是"实体插值"：把样本按 tick 缓存起来，渲染时不用"最新 tick"，
/// 而用落后 <see cref="NetcodeConfig.InterpolationDelayTicks"/> 的**播放时钟**，
/// 在过去两个真实样本之间插值。代价是别人比你晚一点点，换来的是完全平滑、
/// 绝不瞬移（Gambetta FPM III）。
///
/// 为什么不用 dead reckoning：我们的生物是行为树 + ORCA + 地形碰撞，方向随时反转，
/// 属于"外推必失效"那一类。外推只保留给"播放时钟跑过最新样本"这一种情况，且有界。
/// </summary>
public sealed class RemoteEntity<TState> where TState : struct
{
    private readonly IStateSpace<TState> _space;
    private readonly NetcodeConfig _config;
    private readonly List<SnapshotEntry<TState>> _samples;
    private ulong _epoch;

    public RemoteEntity(IStateSpace<TState> space, NetcodeConfig? config = null, int capacity = 32)
    {
        _space = space ?? throw new ArgumentNullException(nameof(space));
        _config = config ?? new NetcodeConfig();
        _samples = new List<SnapshotEntry<TState>>(capacity);
    }

    public int Buffered => _samples.Count;
    public bool HasData => _samples.Count > 0;
    public long LatestTick => _samples.Count > 0 ? _samples[^1].Tick : long.MinValue;

    /// <summary>推入一份样本。过旧（tick 比最新还小）直接丢弃；同 tick 后者胜。</summary>
    public bool Push(long tick, in TState state, ulong epoch = 0)
    {
        if (epoch != _epoch && epoch != 0) Clear(epoch);
        if (_samples.Count > 0)
        {
            if (tick < _samples[^1].Tick) return false;
            if (tick == _samples[^1].Tick)
            {
                _samples[^1] = new SnapshotEntry<TState>(tick, state, 0);
                return true;
            }
        }

        _samples.Add(new SnapshotEntry<TState>(tick, state, 0));
        if (_samples.Count > 64) _samples.RemoveAt(0);
        return true;
    }

    /// <summary>按播放时钟采样。</summary>
    public TState Sample(double nowTick, out RemoteSampleReport report)
    {
        if (_samples.Count == 0)
        {
            report = new RemoteSampleReport(nowTick, 0, 0, 0f, RemoteSampleKind.Empty, 0, false, 0);
            return default;
        }

        var latest = _samples[^1];
        var playout = nowTick - _config.InterpolationDelayTicks;
        var starved = nowTick - latest.Tick > _config.StallMs / _config.TickMs;

        // 播放时钟还在第一个样本之前：没有更早的数据，只能用它。
        if (playout <= _samples[0].Tick)
        {
            report = new RemoteSampleReport(
                playout, _samples[0].Tick, _samples[0].Tick, 0f,
                RemoteSampleKind.Interpolated, _samples.Count, starved, nowTick - latest.Tick);
            return _samples[0].State;
        }

        // 找 playout 落在哪两个样本之间。
        for (var i = 1; i < _samples.Count; i++)
        {
            if (_samples[i].Tick < playout) continue;
            var a = _samples[i - 1];
            var b = _samples[i];
            var span = b.Tick - a.Tick;
            if (span <= 0)
            {
                report = new RemoteSampleReport(
                    playout, b.Tick, b.Tick, 1f,
                    RemoteSampleKind.Interpolated, _samples.Count, starved, nowTick - latest.Tick);
                return b.State;
            }

            var alpha = (float)Math.Clamp((playout - a.Tick) / span, 0.0, 1.0);
            report = new RemoteSampleReport(
                playout, a.Tick, b.Tick, alpha,
                RemoteSampleKind.Interpolated, _samples.Count, starved, nowTick - latest.Tick);
            return _space.Lerp(a.State, b.State, alpha);
        }

        // 播放时钟跑过了最新样本：样本迟到/丢了。
        var beyond = playout - latest.Tick;
        if (_config.Extrapolate && _samples.Count >= 2 && beyond <= _config.MaxExtrapolationTicks)
        {
            var a = _samples[^2];
            var span = latest.Tick - a.Tick;
            if (span > 0)
            {
                // 用**最后一段真实样本**的速度外推（不是服务端声明的速度，
                // 所以贴墙/被挡时那一段本来就没速度，外推自然不动）。
                var t = 1f + (float)(beyond / span);
                report = new RemoteSampleReport(
                    playout, a.Tick, latest.Tick, t,
                    RemoteSampleKind.Extrapolated, _samples.Count, starved, nowTick - latest.Tick);
                return _space.Lerp(a.State, latest.State, t);
            }
        }

        // 到顶/不可外推：冻结在最新样本（绝不倒退）。
        report = new RemoteSampleReport(
            playout, latest.Tick, latest.Tick, 1f,
            RemoteSampleKind.Frozen, _samples.Count, starved, nowTick - latest.Tick);
        return latest.State;
    }

    public void Clear(ulong epoch = 0)
    {
        _samples.Clear();
        if (epoch != 0) _epoch = epoch;
    }
}
