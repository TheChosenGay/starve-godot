namespace Starve.Protocol;

/// <summary>路由契约：与 starve/pkg/proto/routes.go 保持同步。</summary>
public static class Routes
{
    public const string Login = "gate.login";
    public const string Move = "world.player.move";
    public const string Gather = "world.player.gather";
    public const string Attack = "world.player.attack";
    public const string Pickup = "world.player.pickup";
    public const string Use = "world.player.use";
    public const string Equip = "world.player.equip";
    public const string Chop = "world.player.chop";
    public const string Mine = "world.player.mine";
    public const string Automate = "world.player.automate";
    public const string Craft = "world.player.craft";
    public const string CancelCraft = "world.player.craft.cancel";
    public const string Split = "world.player.split";
    public const string Drop = "world.player.drop";
    public const string Build = "world.build";
    public const string BuildCheck = "world.build.check";
    public const string Place = "world.place";
    public const string Demolish = "world.demolish";
    public const string Save = "game.save";

    public const string Snapshot = "world.snapshot";
    public const string SnapshotDelta = "world.snapshot.delta";
    public const string CraftDone = "world.craft.done";
    public const string Config = "world.config";
    public const string WeatherFrame = "world.weather.frame";
}
