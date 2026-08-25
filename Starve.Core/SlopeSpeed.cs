using System;
using System.Numerics;

namespace Starve.Core;

/// <summary>
/// 坡度速度：按未旋转等距投影的边长比缩放世界速度。
/// 与服务端 worldmap.SlopeFactorAt 同一公式；不读相机缩放/旋转。
/// </summary>
public static class SlopeSpeed
{
    public const float FactorMin = 0.35f;
    public const float FactorMax = 1f;

    public static float Factor(float wx, float wy, int dx, int dy, Func<float, float, float>? heightAt)
    {
        if (dx == 0 && dy == 0) return 1f;
        if (heightAt is null) return 1f;

        var h0 = heightAt(wx, wy);
        var h1 = heightAt(wx + dx, wy + dy);
        var p0 = IsoMath.WorldToLocal(wx, wy, h0);
        var flat = IsoMath.WorldToLocal(wx + dx, wy + dy, h0);
        var sloped = IsoMath.WorldToLocal(wx + dx, wy + dy, h1);
        var flatLen = Vector2.Distance(p0, flat);
        var slopedLen = Vector2.Distance(p0, sloped);
        if (slopedLen < 1e-6f) return 1f;
        return Math.Clamp(flatLen / slopedLen, FactorMin, FactorMax);
    }
}
