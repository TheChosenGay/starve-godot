using System;

namespace GodotClient.Game;

public readonly record struct SpiritVisualSample(float BobOffset, float Alpha, float Brightness);

/// <summary>玩家生死视觉状态与魂魄脉冲/漂浮采样，不接触 Godot 节点。</summary>
public sealed class SpiritPresentationState
{
    public bool IsDead { get; private set; }
    private double _elapsedMs;

    public bool SetDead(bool dead)
    {
        if (IsDead == dead) return false;
        IsDead = dead;
        _elapsedMs = 0;
        return true;
    }

    public SpiritVisualSample Advance(double deltaMs)
    {
        if (!IsDead) return new SpiritVisualSample(0, 1, 1);
        _elapsedMs += Math.Max(0, deltaMs);
        var phase = (float)(_elapsedMs / 1500d * Math.PI * 2);
        return new SpiritVisualSample(
            MathF.Sin(phase) * 3f,
            0.58f + MathF.Sin(phase) * 0.07f,
            1.12f + MathF.Sin(phase) * 0.08f);
    }
}
