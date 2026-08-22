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
    int Signature,
    int Hunger = 0,
    int HungerMaximum = 100,
    float HungerRatio = 0)
{
    public static HudVitalsViewModel Create(
        int current,
        int maximum,
        bool isDead,
        int hunger = 0,
        int hungerMaximum = 100)
    {
        var safeMax = Math.Max(0, maximum);
        var safeCurrent = Math.Clamp(current, 0, safeMax);
        var ratio = safeMax > 0 ? safeCurrent / (float)safeMax : 0;
        var safeHungerMax = Math.Max(1, hungerMaximum);
        var safeHunger = Math.Clamp(hunger, 0, safeHungerMax);
        var hungerRatio = safeHunger / (float)safeHungerMax;
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
            HashCode.Combine(safeCurrent, safeMax, isDead, safeHunger),
            safeHunger,
            safeHungerMax,
            hungerRatio);
    }
}
