using Google.Protobuf;
using Starve.Game.V1;

namespace Starve.Protocol.World;

public static class EntityViewExt
{
    /// <summary>
    /// 掉落物组件读取：M7 起服务端把 Loot 改名为 Lootable（载荷仍是 game.Loot），
    /// 这里兼容两个名字，旧档/旧服务端也不会漏读。
    /// </summary>
    public static Loot? LootOf(this EntityView view) =>
        view.Get("Lootable", Loot.Parser) ?? view.Get("Loot", Loot.Parser);
}
