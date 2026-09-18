using Godot;
using Starve.Game.V1;
using Starve.Protocol.World;

namespace GodotClient.Game;

/// <summary>
/// 3D 主场景外观目录。要换模型只改这里。
/// 玩家/野猪：猪人 walk/run。兔/狼/鹿：Quaternius Animal Pack。
/// Boss：见 <see cref="BossModelPath"/>（模型缺位时退回 <see cref="BossPlaceholder3D"/>）。
/// </summary>
public static class ActorCatalog3D
{
    public const string AnimalsDir = "res://assets/models/quaternius-animals/";

    /// <summary>
    /// Boss 模型路径。**美术模型就位后把 glb 放到这个路径即可**：
    /// <c>&lt;仓库&gt;/GodotClient/assets/models/boss/boss.glb</c>
    /// （目录约定与 <see cref="AnimalsDir"/> 一致，都在 <c>res://assets/models/</c> 下；
    /// <c>assets</c> 是指向 asset-starve 子模块的软链）。要换文件名/换目录只改这一行，
    /// 其余代码不用动。模型还没放进来时会自动退回占位体，不会缺资源报错。
    /// </summary>
    public const string BossModelPath = "res://assets/models/boss/boss.glb";

    /// <summary>
    /// Boss 视觉缩放。真模型与占位体共用这一个常量，保证"换模型前后体型不突然变"。
    /// 缺省 1.0 = 按模型自身导入尺寸原样放进世界（1 世界单位 = 1 格，见 IsoCamera3D.WorldUnit）。
    ///
    /// 注意：这只是美术资源就位前的缺省值，**最终体型应由服务端 <c>body_radius/body_height</c>
    /// 决定**——服务端用 <c>cmd/modelcollide</c> 从模型网格推导出碰撞尺寸后，通过 Collide
    /// 组件下发（客户端本地预测也已经用同一个半径，见 GameRoot 的 SetBodyRadius）。
    /// 等配置对齐，把这里改成同一个换算结果即可，不用再猜。
    /// </summary>
    public const float BossModelScale = 1f;

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
            (int)CreatureKind.Boss => CreateBoss(),
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

    /// <summary>
    /// Boss。模型存在就按 <see cref="CreateAnimal"/> 的写法挂 <see cref="RiggedActor3D"/>，
    /// 模型不存在就退回 <see cref="BossPlaceholder3D"/>——**不能返回 null，也不能只挂一行
    /// "缺少 xxx" 的字**：Boss 是行为树驱动的多阶段敌人，资源没到位时场景里必须还能看到
    /// 一个可辨认的目标，否则"投弹/闪现/锤地/嚎叫"四个技能都无从目视验证。
    /// </summary>
    public static Node3D CreateBoss()
    {
        if (!ResourceLoader.Exists(BossModelPath))
            return new BossPlaceholder3D();

        return new RiggedActor3D
        {
            ModelPath = BossModelPath,
            ModelScale = BossModelScale,
            MarkerLabel = "首领",
            // Boss 体型大、节奏慢：走路动画比小动物慢一点（跑动阈值沿用同一套手感）。
            WalkTilesPerSec = 3.2f,
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
