using System;
using System.Collections.Generic;

namespace GodotClient.Game;

public static class RigPresentationMetrics
{
    public const float FishmanVisualHeight = 64f;
    public const float SpiderVisualHeight = 48f;
    public const float SpiderSubjectHeight = 520f;
    public const float SpiderFootY = 820f / 1024f;
    public const double AttackDurationMs = 800;
    public const double AttackImpactMs = 400;
    public const float FishmanAttackFps = 18.75f;
    public const float LizardAttackFps = 10f;
}

/// <summary>按朝向选择 idle/walk 剪辑；缺侧/背时回退正面，再回退 idle。</summary>
public static class DirectionalSpriteClip
{
    public static string Locomotion(bool moving, bool side, bool back, Func<string, bool> hasClip)
    {
        var prefix = moving ? "walk" : "idle";
        var named = back ? prefix + "_back" : side ? prefix + "_side" : prefix;
        if (hasClip(named)) return named;
        if (hasClip(prefix)) return prefix;
        return hasClip("idle") ? "idle" : named;
    }
}

public readonly record struct DirectionalPartGeometry(
    float Width,
    float Height,
    float AnchorX,
    float AnchorY,
    float BaseScale);

public readonly record struct DirectionalBounds(float Left, float Top, float Right, float Bottom)
{
    public float Width => Right - Left;
    public float Height => Bottom - Top;
}

public readonly record struct DirectionalRigNormalization(
    float Scale,
    float OffsetX,
    float OffsetY,
    DirectionalBounds SourceBounds)
{
    public DirectionalBounds NormalizedBounds => new(
        SourceBounds.Left * Scale + OffsetX,
        SourceBounds.Top * Scale + OffsetY,
        SourceBounds.Right * Scale + OffsetX,
        SourceBounds.Bottom * Scale + OffsetY);
}

/// <summary>将 tight-crop 分件装配统一缩放到目标高度，并对齐水平中心与 bottom=0 脚底线。</summary>
public static class DirectionalRigNormalizer
{
    public static DirectionalRigNormalization Normalize(
        IReadOnlyList<DirectionalPartGeometry> parts,
        float targetHeight)
    {
        if (parts.Count == 0) throw new ArgumentException("directional rig requires parts", nameof(parts));
        var first = BoundsOf(parts[0]);
        var left = first.Left;
        var top = first.Top;
        var right = first.Right;
        var bottom = first.Bottom;
        for (var i = 1; i < parts.Count; i++)
        {
            var bounds = BoundsOf(parts[i]);
            left = MathF.Min(left, bounds.Left);
            top = MathF.Min(top, bounds.Top);
            right = MathF.Max(right, bounds.Right);
            bottom = MathF.Max(bottom, bounds.Bottom);
        }

        var source = new DirectionalBounds(left, top, right, bottom);
        if (source.Height <= 0) throw new ArgumentOutOfRangeException(nameof(parts), "rig height must be positive");
        var scale = targetHeight / source.Height;
        return new DirectionalRigNormalization(
            scale,
            -(left + right) * 0.5f * scale,
            -bottom * scale,
            source);
    }

    private static DirectionalBounds BoundsOf(DirectionalPartGeometry part)
    {
        var halfWidth = part.Width * part.BaseScale * 0.5f;
        var halfHeight = part.Height * part.BaseScale * 0.5f;
        return new DirectionalBounds(
            part.AnchorX - halfWidth,
            part.AnchorY - halfHeight,
            part.AnchorX + halfWidth,
            part.AnchorY + halfHeight);
    }
}
