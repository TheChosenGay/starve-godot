using Godot;
using Starve.Game.V1;
using Starve.Protocol.World;

namespace GodotClient.Game;

public readonly record struct EntityStyle(
    Color Color,
    float Radius,
    bool IsFire,
    bool IsTree = false,
    bool IsWorkbench = false,
    bool IsHauntable = false,
    bool IsFlower = false,
    bool IsShrub = false,
    // IsLit 火堆"着没着"。契约：服务端用 **HeatSource 组件的存在/移除**表达，
    // 移除会走增量快照的 removed 通道，客户端不需要新字段。
    // IsFire 只说"该用火盆外观"（类型），IsLit 才说"现在在烧"（状态），
    // 两者分开是因为熄灭的火堆仍然是个火堆——底座还在，只是没火。
    bool IsLit = true);

/// <summary>实体类型 → 占位色/尺寸；2D 菱形与 3D 立方体共用。</summary>
public static class EntityVisual
{
    public static EntityStyle StyleFor(EntityView view)
    {
        if (view.Get("Hauntable", Hauntable.Parser) is not null)
            return new EntityStyle(
                new Color(0.55f, 0.85f, 1f),
                13,
                false,
                IsHauntable: true);
        if (view.Get("Player", Player.Parser) is not null)
            return new EntityStyle(new Color(0.31f, 0.75f, 0.37f), 10, false);
        // 掉落物优先于 Dead：挖完的矿/树是 Dead+Loot，应显示成可拾取的黄色，
        // 而不是尸体的灰色（否则看不出能捡）。
        if (view.LootOf() is not null)
            return new EntityStyle(new Color(1f, 0.85f, 0.31f), 6, false);
        if (view.Components.ContainsKey("Dead"))
            return new EntityStyle(new Color(0.47f, 0.47f, 0.47f), 8, false);

        var station = view.Get("Workstation", Workstation.Parser);
        if (station is not null)
            // 地图种子的工作站营火服务端没有 HeatSource（不参与燃料循环），保持常亮。
            return (int)station.Type == 1
                ? new EntityStyle(new Color(1f, 0.55f, 0.26f), 10, true)
                : new EntityStyle(new Color(0.60f, 0.42f, 0.25f), 10, false, IsWorkbench: true);

        var scenery = view.Get("Scenery", Scenery.Parser);
        if (scenery?.Kind == ItemKind.Shrub)
            return new EntityStyle(
                new Color(0.37f, 0.57f, 0.31f),
                8,
                false,
                IsShrub: true);

        var pickable = view.Get("Pickable", WorkTarget.Parser);
        var reactive = view.Get("Choppable", WorkTarget.Parser)
            ?? view.Get("Minable", WorkTarget.Parser)
            ?? pickable;
        if (reactive is not null)
        {
            var kind = reactive.Kind;
            var isTree = view.Get("Choppable", WorkTarget.Parser) is not null;
            var isFlower = pickable?.Kind == ItemKind.Flower;
            return new EntityStyle(kind switch
            {
                ItemKind.Berry => new Color(0.89f, 0.34f, 0.30f),
                ItemKind.Wood => new Color(0.60f, 0.42f, 0.25f),
                ItemKind.Flint => new Color(0.60f, 0.63f, 0.66f),
                ItemKind.Meat => new Color(0.85f, 0.42f, 0.31f),
                ItemKind.Flower => new Color(0.95f, 0.60f, 0.76f),
                _ => new Color(0.71f, 0.54f, 0.85f),
            }, isTree ? 16 : isFlower ? 5 : 7, false, isTree, IsFlower: isFlower);
        }

        var workable = view.Get("Workable", Workable.Parser);
        if (workable is not null)
        {
            return new EntityStyle((int)workable.Kind switch
            {
                1 => new Color(0.89f, 0.34f, 0.30f),
                2 => new Color(0.60f, 0.42f, 0.25f),
                3 => new Color(0.60f, 0.63f, 0.66f),
                4 => new Color(0.85f, 0.42f, 0.31f),
                _ => new Color(0.71f, 0.54f, 0.85f),
            }, (int)workable.Kind == 2 ? 16 : 7, false, (int)workable.Kind == 2);
        }

        var creature = view.Get("Creature", Creature.Parser);
        if (creature is not null)
        {
            return new EntityStyle((int)creature.Kind switch
            {
                1 => new Color(0.63f, 0.44f, 0.31f),
                2 => new Color(0.54f, 0.56f, 0.60f),
                3 => new Color(0.36f, 0.25f, 0.22f),
                4 => new Color(0.84f, 0.72f, 0.60f),
                5 => new Color(0.29f, 0.14f, 0.35f),
                6 => new Color(0.22f, 0.55f, 0.58f),
                7 => new Color(0.55f, 0.75f, 0.35f),
                _ => new Color(1f, 1f, 1f),
            }, 9, false);
        }

        var building = view.Get("Building", Building.Parser);
        if (building is not null)
            // 火堆：外观仍由 Kind 决定（放下就一直是火盆），但**火焰只在带 HeatSource
            // 时显示**——燃料烧完服务端会移除该组件，这里随即变成一堆冷灰。
            return (int)building.Kind == 1
                ? new EntityStyle(new Color(1f, 0.55f, 0.26f), 10, building.Placed, IsLit: view.Has("HeatSource"))
                : new EntityStyle(new Color(0.60f, 0.42f, 0.25f), 8, false);

        return new EntityStyle(new Color(1f, 1f, 1f), 8, false);
    }

    public static bool IsDepletedFlower(EntityView view) =>
        view.Get("Pickable", WorkTarget.Parser) is
            { Kind: ItemKind.Flower, WorkLeft: <= 0 };
}
