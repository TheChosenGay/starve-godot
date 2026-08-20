using System;
using Starve.Core;

namespace GodotClient.Game;

/// <summary>
/// Godot 侧低频拉取适配器。Core 只暴露稳定快照，不依赖任何遥测 SDK。
/// </summary>
public sealed class MovementDiagnosticsSampler
{
    private readonly Func<MovementDiagnostics> _read;
    private readonly long _intervalMs;
    private long _nextSampleAt;
    private long _lastSampleAt;
    private bool _hasSample;
    private MovementDiagnostics _last;

    public MovementDiagnosticsSampler(Func<MovementDiagnostics> read, long intervalMs = 1000)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (intervalMs <= 0) throw new ArgumentOutOfRangeException(nameof(intervalMs));
        _read = read;
        _intervalMs = intervalMs;
    }

    /// <summary>到采样周期时返回快照；changed 表示相对上次采样有变化。</summary>
    public bool TrySample(long nowMs, out MovementDiagnostics diagnostics, out bool changed)
    {
        if (_hasSample && nowMs >= _lastSampleAt && nowMs < _nextSampleAt)
        {
            diagnostics = _last;
            changed = false;
            return false;
        }

        diagnostics = _read();
        changed = !_hasSample || diagnostics != _last;
        _last = diagnostics;
        _hasSample = true;
        _lastSampleAt = nowMs;
        _nextSampleAt = nowMs > long.MaxValue - _intervalMs
            ? long.MaxValue
            : nowMs + _intervalMs;
        return true;
    }
}
