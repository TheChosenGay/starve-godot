using Godot;
using Starve.Game.V1;
using Starve.Protocol.World;

namespace GodotClient.Game;

/// <summary>
/// 3D 主场景角色外观目录。要换模型只改这里。
/// 当前：玩家（原鱼人）和野猪都用 Meshy 猪人。
/// </summary>
public static class ActorCatalog3D
{
    public static Node3D? TryCreate(EntityView view)
    {
        if (view.Get("Player", Player.Parser) is not null)
            return CreatePigman();

        var creature = view.Get("Creature", Creature.Parser);
        if (creature is null) return null;
        return (int)creature.Kind switch
        {
            (int)CreatureKind.Boar => CreatePigman(),
            (int)CreatureKind.Lizard => CreateSprite(CharacterPreviewKind.Lizard),
            (int)CreatureKind.Spider => CreateSprite(CharacterPreviewKind.Spider),
            (int)CreatureKind.Fishman => CreateSprite(CharacterPreviewKind.Fishman),
            _ => null,
        };
    }

    public static Node3D CreatePigman() => new PigmanActor3D
    {
        Clip = PigmanActor3D.PigmanClip.Walk,
        Playing = false,
        YawDegrees = 0f,
        ApplyToon = true,
    };

    private static Node3D CreateSprite(CharacterPreviewKind kind) => new ActorPreview3D
    {
        Kind = kind,
        Display = CharacterPreviewDisplay.SpriteArt,
    };
}
