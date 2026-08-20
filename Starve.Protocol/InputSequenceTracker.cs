namespace Starve.Protocol;

/// <summary>
/// 单次登录输入流的序号与确认状态。线程安全；不依赖 Godot 或网络实现。
/// epoch 变化时清空 sent/ack，旧 epoch 的迟到 ACK 不生效。
/// </summary>
public sealed class InputSequenceTracker
{
    public const ulong MaxPendingForPrediction = 4;

    private readonly object _gate = new();
    private ulong _epoch;
    private ulong _lastSent;
    private ulong _lastAccepted;

    public ulong Epoch { get { lock (_gate) return _epoch; } }
    public ulong LastSent { get { lock (_gate) return _lastSent; } }
    public ulong LastAccepted { get { lock (_gate) return _lastAccepted; } }

    public ulong Pending
    {
        get
        {
            lock (_gate)
                return _lastSent >= _lastAccepted ? _lastSent - _lastAccepted : 0;
        }
    }

    public bool CanPredict => Epoch != 0 && Pending <= MaxPendingForPrediction;

    public void Begin(ulong epoch)
    {
        if (epoch == 0) throw new ArgumentOutOfRangeException(nameof(epoch));
        lock (_gate)
        {
            _epoch = epoch;
            _lastSent = 0;
            _lastAccepted = 0;
        }
    }

    public ulong Next()
    {
        lock (_gate)
        {
            if (_epoch == 0) throw new InvalidOperationException("input epoch is not initialized");
            return ++_lastSent;
        }
    }

    public void Acknowledge(ulong epoch, ulong seq)
    {
        lock (_gate)
        {
            if (epoch == 0 || epoch != _epoch || seq <= _lastAccepted) return;
            _lastAccepted = Math.Min(seq, _lastSent);
        }
    }
}
