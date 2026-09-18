namespace Starve.Netcode;

/// <summary>一条已发送的输入。</summary>
public readonly record struct InputRecord<TAction>(
    ulong Seq,
    ulong Epoch,
    double BaseTick,
    double EffectiveTick,
    TAction Action)
    where TAction : struct;

/// <summary>
/// 本端输入历史（<c>localCache</c>）：seq / epoch / 生效 tick / 操作。
///
/// 两个用途：
/// <list type="number">
/// <item>重放时按 tick 取"当时那条操作"；</item>
/// <item>按服务端回传的 appliedSeq 裁剪已确认的部分。</item>
/// </list>
///
/// 记录规模：**序号锚定模式**下每个 tick 采样一条（长按也是每秒 20 条，
/// 因为"按住"必须持续产生操作，服务端才有密集的比对点、每条的时长也才统一）；
/// 旧路径（意图变化才记一条）保留兼容。内部用顺序表即可（定容后不再分配）。
/// </summary>
public sealed class InputHistory<TAction> where TAction : struct
{
    private readonly List<InputRecord<TAction>> _records;
    private readonly int _capacity;
    private TAction _latest;
    private bool _hasLatest;
    private ulong _latestSeq;
    private ulong _epoch;

    public InputHistory(int capacity = 64)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _records = new List<InputRecord<TAction>>(capacity);
    }

    /// <summary>当前会话代号。</summary>
    public ulong Epoch => _epoch;

    /// <summary>已记录的最大 seq。</summary>
    public ulong LatestSeq => _latestSeq;

    /// <summary>未确认的记录条数。</summary>
    public int Count => _records.Count;

    /// <summary>当前意图（最后一条操作）。重放时 tick 超出所有记录范围就用它兜底。</summary>
    public TAction Latest => _latest;

    /// <summary>是否已有过任何记录。</summary>
    public bool HasLatest => _hasLatest;

    /// <summary>
    /// 记录一条输入。<paramref name="effectiveTick"/> 由调用方用 ServerClock 算好
    /// （组件不依赖时钟）。epoch 变化会自动清空历史。
    /// </summary>
    public void Record(ulong seq, ulong epoch, double baseTick, double effectiveTick, in TAction action)
    {
        if (epoch != _epoch) Clear(epoch);
        if (_hasLatest && seq <= _latestSeq) return; // 乱序/重复：忽略

        _records.Add(new InputRecord<TAction>(seq, epoch, baseTick, effectiveTick, action));
        if (_records.Count > _capacity) _records.RemoveAt(0);

        _latest = action;
        _hasLatest = true;
        _latestSeq = seq;
    }

    /// <summary>
    /// 取某条记录的"原始基准 tick"（= 发送时刻的服务端 tick，**不含**单程延迟估计）。
    /// 与"服务端在哪个 tick 真正应用了它"相减，就能标定出单程延迟。
    /// </summary>
    public bool TryGetBaseTick(ulong seq, out double baseTick)
    {
        for (var i = _records.Count - 1; i >= 0; i--)
        {
            if (_records[i].Seq != seq) continue;
            baseTick = _records[i].BaseTick;
            return true;
        }

        baseTick = 0;
        return false;
    }

    /// <summary>
    /// 服务端已确认（烘进权威位置）到 appliedSeq，丢弃不超过它的记录。
    ///
    /// ⚠️ <paramref name="keepLast"/>：<b>必须保留最近若干条</b>，不能全裁掉。
    /// 重放窗口是 <c>[快照 tick, 现在]</c>，而这段区间用到的意图往往**已经被确认**了；
    /// 全裁掉的话 <see cref="TryActionAtTick"/> 查不到就回落到 <c>Latest</c>（最新方向），
    /// 于是整段重放都用错方向 —— 且窗口越长错得越多（实测误差随延迟线性增长：
    /// 0ms→0.02 格、100ms→0.23、300ms→0.97）。意图变化很少（每秒几条），留存成本可忽略。
    /// </summary>
    public void Acknowledge(ulong appliedSeq, int keepLast = 0)
    {
        var drop = 0;
        while (drop < _records.Count && _records[drop].Seq <= appliedSeq) drop++;
        var limit = _records.Count - Math.Max(0, keepLast);
        if (drop > limit) drop = Math.Max(0, limit);
        if (drop > 0) _records.RemoveRange(0, drop);
        // 注意：Latest（当前意图）不清 —— 它要继续为"还没有记录的 tick"兜底。
    }

    /// <summary>取 tick 时刻生效的操作：最后一条 EffectiveTick &lt;= tick 的记录；没有就用 Latest。</summary>
    public bool TryActionAtTick(double tick, out TAction action)
    {
        for (var i = _records.Count - 1; i >= 0; i--)
        {
            if (_records[i].EffectiveTick <= tick)
            {
                action = _records[i].Action;
                return true;
            }
        }

        if (_hasLatest)
        {
            action = _latest;
            return true;
        }

        action = default;
        return false;
    }

    /// <summary>清空历史（换 epoch / 重连）。<paramref name="epoch"/> = 0 表示保持当前代号。</summary>
    public void Clear(ulong epoch = 0)
    {
        _records.Clear();
        _hasLatest = false;
        _latestSeq = 0;
        _latest = default;
        if (epoch != 0) _epoch = epoch;
    }
}
