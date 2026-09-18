namespace Starve.Netcode;

/// <summary>权威快照的一条记录。</summary>
public readonly record struct SnapshotEntry<TState>(
    long Tick,
    TState State,
    ulong AppliedSeq)
    where TState : struct;

/// <summary>
/// 权威快照窗口（<c>netCache</c>）。
///
/// 约定（写死，别让上层替它兜底）：
/// <list type="bullet">
/// <item><b>按 tick 有序</b>：插入时若 tick 比窗口里最新的还旧 → 直接丢弃（乱序容忍）；</item>
/// <item><b>同 tick 后者胜</b>：覆盖最后一条（顺带更新 appliedSeq）；</item>
/// <item>epoch 变化 → 清空。</item>
/// </list>
/// 因为"旧的直接丢"，插入后天然有序，不需要排序。
/// </summary>
public sealed class SnapshotWindow<TState> where TState : struct
{
    private readonly List<SnapshotEntry<TState>> _entries;
    private readonly int _capacity;
    private ulong _epoch;

    public SnapshotWindow(int capacity = 64)
    {
        if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _entries = new List<SnapshotEntry<TState>>(capacity);
    }

    public int Count => _entries.Count;
    public bool HasData => _entries.Count > 0;
    public ulong Epoch => _epoch;
    public long LatestTick => _entries.Count > 0 ? _entries[^1].Tick : long.MinValue;
    public long OldestTick => _entries.Count > 0 ? _entries[0].Tick : long.MinValue;

    /// <summary>插入一份快照。返回 false = 因过旧被丢弃。</summary>
    public bool Insert(long tick, in TState state, ulong appliedSeq, ulong epoch)
    {
        if (epoch != _epoch) Clear(epoch);
        if (_entries.Count > 0 && tick < _entries[^1].Tick) return false;

        if (_entries.Count > 0 && tick == _entries[^1].Tick)
        {
            _entries[^1] = new SnapshotEntry<TState>(tick, state, appliedSeq);
            return true;
        }

        _entries.Add(new SnapshotEntry<TState>(tick, state, appliedSeq));
        if (_entries.Count > _capacity) _entries.RemoveAt(0);
        return true;
    }

    public bool TryLatest(out SnapshotEntry<TState> entry)
    {
        if (_entries.Count == 0)
        {
            entry = default;
            return false;
        }

        entry = _entries[^1];
        return true;
    }

    /// <summary>取最新之前的那一条（用于"同一 tick 跨度"的速度对比）。</summary>
    public bool TryPrevious(out SnapshotEntry<TState> entry)
    {
        if (_entries.Count < 2)
        {
            entry = default;
            return false;
        }

        entry = _entries[^2];
        return true;
    }

    public void Clear(ulong epoch = 0)
    {
        _entries.Clear();
        if (epoch != 0) _epoch = epoch;
    }
}
