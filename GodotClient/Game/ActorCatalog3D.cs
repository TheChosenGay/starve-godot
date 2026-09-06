using Godot;
using Starve.Game.V1;
using Starve.Protocol.World;

namespace GodotClient.Game;

/// <summary>
/// 3D 主场景外观目录。要换模型只改这里。
/// 玩家/野猪：猪人 walk/run。兔/狼/鹿：Quaternius Animal Pack。
/// </summary>
public static class ActorCatalog3D
{
    public const string AnimalsDir = "res://assets/models/quaternius-animals/";

    public static Node3D? TryCreate(EntityView view)
    {
        if (view.Get("Player", Player.Parser) is not null)
            return CreatePigman();

        var pickable = view.Get("Pickable", WorkTarget.Parser);
        if (pickable?.Kind == ItemKind.Flower)
            return new FlowerActor3D { VariantSeed = view.EntityId };

        var scenery = view.Get("Scenery", Scenery.Parser);
        if (scenery?.Kind == ItemKind.Shrub)
            return new ShrubActor3D { VariantSeed = view.EntityId };

        var style = EntityVisual.StyleFor(view);
        if (style.IsTree)
            return new TreeActor3D();

        if (style.IsWorkbench)
            return new AlchemyEngine3D();

        var creature = view.Get("Creature", Creature.Parser);
        if (creature is null) return null;
        return (int)creature.Kind switch
        {
            (int)CreatureKind.Boar => CreatePigman(),
            (int)CreatureKind.Wolf => CreateAnimal("Wolf.glb", 0.48f, "狼"),
            (int)CreatureKind.Deer => CreateAnimal("Deer.glb", 0.40f, "鹿"),
            (int)CreatureKind.Rabbit => CreateAnimal("Fox.glb", 0.22f, "兔"),
            (int)CreatureKind.Lizard => CreateSprite(CharacterPreviewKind.Lizard),
            (int)CreatureKind.Spider => CreateSprite(CharacterPreviewKind.Spider),
            (int)CreatureKind.Fishman => CreateSprite(CharacterPreviewKind.Fishman),
            _ => null,
        };
    }

    public static Node3D CreatePigman() => new PigmanActor3D
    {
        Clip = PigmanActor3D.PigmanClip.Walk,
        YawDegrees = 0f,
        ApplyToon = false,
    };

    public static Node3D CreateAnimal(string fileName, float scale, string marker)
    {
        var path = AnimalsDir + fileName;
        return new RiggedActor3D
        {
            ModelPath = path,
            ModelScale = scale,
            MarkerLabel = marker,
            WalkTilesPerSec = 3.8f,
            RunSpeedThreshold = 13f,
            ApplyToon = false,
        };
    }

    private static Node3D CreateSprite(CharacterPreviewKind kind) => new ActorPreview3D
    {
        Kind = kind,
        Display = CharacterPreviewDisplay.SpriteArt,
    };
}
