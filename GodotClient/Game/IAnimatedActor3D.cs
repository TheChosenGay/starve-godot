using Starve.Game.V1;

namespace GodotClient.Game;

/// <summary>3D 角色表现口：与 2D <see cref="RigNode"/> 对齐，供 EntityLayer3D 驱动。</summary>
public interface IAnimatedActor3D
{
    float ModelScale { get; set; }
    float AnimSpeedMul { get; set; }
    bool ApplyToon { get; set; }

    void SetLocomotion(bool moving, float tilesPerSec = 10f);
    void PlayAction(ActionKind kind);
    void FinishAction();
    void CancelAction();
    void PlayHit();
    void PlayDeath();
    void SetFlash(bool on);
}
