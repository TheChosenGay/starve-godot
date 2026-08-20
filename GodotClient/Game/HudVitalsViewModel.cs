using System;

namespace GodotClient.Game;

public enum VitalTone
{
    Green,
    Yellow,
    Red,
    Spirit,
}

public readonly record struct HudVitalsViewModel(
    int Current,
    int Maximum,
    bool IsDead,
    float Ratio,
    string Text,
    VitalTone Tone,
    int Signature)
{
    public static HudVitalsViewModel Create(int current, int maximum, bool isDead)
    {
        var safeMax = Math.Max(0, maximum);
        var safeCurrent = Math.Clamp(current, 0, safeMax);
        var ratio = safeMax > 0 ? safeCurrent / (float)safeMax : 0;
        var tone = isDead ? VitalTone.Spirit
            : ratio > 0.6f ? VitalTone.Green
            : ratio > 0.3f ? VitalTone.Yellow
            : VitalTone.Red;
        var text = isDead
            ? $"灵魂状态 · 生命 0 / {safeMax}"
            : $"生命 {safeCurrent} / {safeMax}";
        return new HudVitalsViewModel(
            safeCurrent,
            safeMax,
            isDead,
            ratio,
            text,
            tone,
            HashCode.Combine(safeCurrent, safeMax, isDead));
    }
}
