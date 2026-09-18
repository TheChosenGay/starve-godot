using System;

namespace Starve.Core;

public readonly record struct LocomotionSample(bool Moving, float TilesPerSec);

/// <summary>
/// 走路表现只看实际位移。卡住、贴墙、坡上减速都用这一帧走过的格数，
/// 不要用名义 <c>effective_speed</c> 硬播 walk。
/// </summary>
public static class LocomotionPresentation
{
    public const float StopTilesPerSec = 0.8f;

    public static LocomotionSample FromDisplacement(float dx, float dy, float deltaMs, float idleSpeed)
    {
        var actual = deltaMs > 1f
            ? MathF.Sqrt(dx * dx + dy * dy) * 1000f / deltaMs
            : 0f;
        var moving = actual > StopTilesPerSec;
        return new LocomotionSample(moving, moving ? actual : MathF.Max(0f, idleSpeed));
    }
}
