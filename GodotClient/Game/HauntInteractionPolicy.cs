using System;
using Starve.Game.V1;

namespace GodotClient.Game;

public enum HauntValidation
{
    Allowed,
    ActorAlive,
    TargetMissing,
    Depleted,
    OutOfRange,
}

/// <summary>作祟输入的纯逻辑规则，供 GameRoot 和单元测试共用。</summary>
public static class HauntInteractionPolicy
{
    public static bool IsGameplayLocked(ActionPresentationStatus? action) =>
        action is { } current &&
        (current.Kind == ActionKind.Haunt || current.Uninterruptible);

    public static HauntValidation Validate(
        bool actorDead,
        bool targetHauntable,
        int remainingUses,
        int actorX,
        int actorY,
        int targetX,
        int targetY,
        int footprintWidth = 1,
        int footprintHeight = 1)
    {
        if (!actorDead) return HauntValidation.ActorAlive;
        if (!targetHauntable) return HauntValidation.TargetMissing;
        if (remainingUses <= 0) return HauntValidation.Depleted;

        var maxX = targetX + Math.Max(1, footprintWidth) - 1;
        var maxY = targetY + Math.Max(1, footprintHeight) - 1;
        var nearestX = Math.Clamp(actorX, targetX, maxX);
        var nearestY = Math.Clamp(actorY, targetY, maxY);
        return Math.Abs(actorX - nearestX) + Math.Abs(actorY - nearestY) <= 2
            ? HauntValidation.Allowed
            : HauntValidation.OutOfRange;
    }
}
