namespace Starve.Netcode;

/// <summary>
/// 墙钟 ↔ 服务端 tick 的稳定映射：<c>tick = (nowMs - offsetMs) / TickMs</c>。
///
/// 为什么要它：如果每个快照都把"服务端 tick 对应哪一刻"重新锚定成"当前帧时刻"，
/// 插值时钟的相位会随帧边界抖动（实测 349 帧里 7 帧异常）。
///
/// 校正策略：
/// <list type="bullet">
/// <item><b>会话换了 / tick 倒退 / 偏差大到离谱</b> → 重建基准（<see cref="HardResynced"/>）；</item>
/// <item>其余一律<b>比例 + 限速</b>校正：每次吃掉 <see cref="NetcodeConfig.ClockCorrectionRatio"/>
///   的偏差，且不超过 <see cref="NetcodeConfig.ClockMaxStepMs"/>。</item>
/// </list>
///
/// ⚠️ 早先版本按"偏差 &gt; 阈值就硬同步"，结果**一个迟到 400ms 的包会把时间基准整体挪走**，
/// 于是"重放多少 tick"变成 0、本地玩家在那一下冻住 —— 把一次网络延迟放大成一次可见顿挫。
/// 现在这个坑由比例校正规避。
///
/// 纯逻辑；时间全部由调用方传入（便于单测用合成时间）。
/// </summary>
public sealed class ServerClock
{
    private readonly NetcodeConfig _config;
    private double _offsetMs;
    private long _lastTick = long.MinValue;
    private ulong _epoch;
    private bool _hasEpoch;
    private bool _synced;

    public ServerClock(NetcodeConfig? config = null) => _config = config ?? new NetcodeConfig();

    /// <summary>是否已经用第一份快照建立过基准。</summary>
    public bool HasSync => _synced;

    /// <summary>最近一次 <see cref="OnSnapshot"/> 是否重建了时间基准。</summary>
    public bool HardResynced { get; private set; }

    /// <summary>当前估计的偏移（毫秒）。</summary>
    public double OffsetMs => _offsetMs;

    /// <summary>
    /// 喂一份快照的 tick、它被处理的墙钟、以及会话代号。
    /// <paramref name="maxStepMs"/>：本次允许的最大校正量（默认用配置值）。
    /// 组件会按"距离上次喂时钟过了多少 tick"给预算 —— 因为一次帧里可能补来一整个突发
    /// （延迟恢复/抖动），逐份都吃满限速等于把限速放大 N 倍。
    /// </summary>
    public void OnSnapshot(long serverTick, long nowMs, ulong epoch = 0, double maxStepMs = double.NaN)
    {
        HardResynced = false;
        var observed = nowMs - serverTick * _config.TickMs;
        var limit = double.IsNaN(maxStepMs) ? _config.ClockMaxStepMs : maxStepMs;

        if (!_synced)
        {
            _offsetMs = observed;
            _synced = true;
        }
        else
        {
            var epochChanged = _hasEpoch && epoch != 0 && epoch != _epoch;
            var wentBackwards = serverTick < _lastTick;
            var absurd = Math.Abs(observed - _offsetMs) > _config.ClockResyncMs;

            if (epochChanged || wentBackwards || absurd)
            {
                _offsetMs = observed;
                HardResynced = true;
            }
            else
            {
                var diff = observed - _offsetMs;
                _offsetMs += Math.Clamp(diff * _config.ClockCorrectionRatio, -limit, limit);
            }
        }

        _lastTick = serverTick;
        if (epoch != 0)
        {
            _epoch = epoch;
            _hasEpoch = true;
        }
    }

    /// <summary>当前服务端 tick（含小数）。未同步时返回 0。</summary>
    public double TickAt(long nowMs) => _synced ? (nowMs - _offsetMs) / _config.TickMs : 0.0;

    /// <summary>现在比某份快照新了多少 tick（≈ 网络领先量 + 快照间隔）。</summary>
    public double LeadTicks(long nowMs, long snapshotTick) => TickAt(nowMs) - snapshotTick;
}
