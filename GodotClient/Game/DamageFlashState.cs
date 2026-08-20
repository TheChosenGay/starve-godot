using System;
using Starve.Game.V1;

namespace GodotClient.Game;

/// <summary>本地受击红屏的纯时间曲线；只接受命中本地玩家的权威 HIT。</summary>
public sealed class DamageFlashState
{
    public const double DurationMs = 350;
    public const float HitIntensity = 0.38f;
    public const float MaxIntensity = 0.78f;

    private double _remainingMs;
    private float _peakIntensity;

    public float Alpha
    {
        get
        {
            if (_remainingMs <= 0) return 0;
            var t = (float)Math.Clamp(_remainingMs / DurationMs, 0, 1);
            var smooth = t * t * (3 - 2 * t);
            return _peakIntensity * smooth;
        }
    }

    public bool ApplyImpact(CombatImpactResult result, bool targetsLocalPlayer)
    {
        if (!targetsLocalPlayer || result != CombatImpactResult.Hit) return false;
        _peakIntensity = MathF.Min(MaxIntensity, Alpha + HitIntensity);
        _remainingMs = DurationMs;
        return true;
    }

    public void Advance(double deltaMs)
    {
        _remainingMs = Math.Max(0, _remainingMs - Math.Max(0, deltaMs));
        if (_remainingMs == 0) _peakIntensity = 0;
    }
}
