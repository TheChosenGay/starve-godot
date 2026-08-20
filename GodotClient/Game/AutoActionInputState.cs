using System;

namespace GodotClient.Game;

public enum AutoActionIntent
{
    Any,
    AttackOnly,
}

/// <summary>空格/F 独立 held 状态：按下立即触发，之后按固定间隔节流重复。</summary>
public sealed class AutoActionInputState
{
    public const long RepeatIntervalMs = 150;

    private HeldState _any;
    private HeldState _attackOnly;

    public bool IsHeld(AutoActionIntent intent) => State(intent).Held;

    public void Press(AutoActionIntent intent, long nowMs, Action<AutoActionIntent> trigger)
    {
        ref var state = ref State(intent);
        if (state.Held) return;
        state = new HeldState(true, checked(nowMs + RepeatIntervalMs));
        trigger(intent);
    }

    public void Release(AutoActionIntent intent)
    {
        ref var state = ref State(intent);
        state = default;
    }

    public void Tick(long nowMs, Action<AutoActionIntent> trigger)
    {
        Tick(ref _any, AutoActionIntent.Any, nowMs, trigger);
        Tick(ref _attackOnly, AutoActionIntent.AttackOnly, nowMs, trigger);
    }

    private static void Tick(
        ref HeldState state,
        AutoActionIntent intent,
        long nowMs,
        Action<AutoActionIntent> trigger)
    {
        if (!state.Held || nowMs < state.NextAtMs) return;
        state.NextAtMs = checked(nowMs + RepeatIntervalMs);
        trigger(intent);
    }

    private ref HeldState State(AutoActionIntent intent)
    {
        if (intent == AutoActionIntent.Any) return ref _any;
        return ref _attackOnly;
    }

    private struct HeldState(bool held, long nextAtMs)
    {
        public bool Held = held;
        public long NextAtMs = nextAtMs;
    }
}
