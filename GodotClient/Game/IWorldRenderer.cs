using System;
using System.Collections.Generic;
using Starve.Core;
using Starve.Game.V1;
using Starve.Protocol;
using Starve.Protocol.World;
using TileMap = Starve.Core.TileMap;

namespace GodotClient.Game;

/// <summary>
/// 世界表现端口：GameRoot 只通过这组方法驱动实体层，2D 菱形/骨骼与 3D 立方体共用。
/// </summary>
public interface IWorldRenderer
{
    void SetSfx(SfxService? sfx);
    void SetOwnId(ulong id);
    void SetNameProvider(Func<EntityView, string?> provider);
    void SetTilemap(TileMap? tm);
    void SetViewRotation(float radians);
    void SetDayLight(float dayLight);
    void SetOwnMoveDir(int dx, int dy);
    void SetOwnFacing(float worldX, float worldY);

    void SyncEntities(IReadOnlyDictionary<ulong, EntityView> entities);
    void UpdatePositions(
        IReadOnlyDictionary<ulong, PositionSmoother> smoothers,
        Func<ulong, bool> isMoving,
        long now,
        System.Numerics.Vector2? ownPos = null);

    void PredictAction(ulong id, ActionKind kind, InputCommandRef command);
    void CancelPredictedAction(ulong id, ulong requestId);
    void CancelActionForMovement(ulong id);
    void CancelActionLocally(ulong id);
    void ApplyActionOutcome(ActionOutcome outcome);
    ActionPresentationStatus? ActionStatusOf(ulong id);

    void ApplyCombatImpact(WorldEvent worldEvent, CombatImpactEvent impact);
    void PredictHit(ulong sourceActionId, ulong targetEntity);
}
