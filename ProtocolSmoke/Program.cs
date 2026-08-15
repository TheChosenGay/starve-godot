using Starve.Game.V1;
using Starve.Protocol;
using Starve.Protocol.World;

// 冒烟测试：不经 Godot，直连网关验证 握手 → 登录 → 全量快照 全链路。
// 用法：dotnet run --project ProtocolSmoke [uid]
var uid = args.Length > 0 ? args[0] : "42";

using var client = new StarveClient();
try
{
    var info = await client.ConnectAsync("ws://localhost:8081/ws", DevTokens.Mint(uid));
    Console.WriteLine($"[连接成功] uid={info.UserId} entity={info.EntityId}");
    Console.WriteLine($"[世界] 实体数 = {client.World.Count}");

    var stations = client.World.Entities.Values
        .Select(v => (v, ws: v.Get("Workstation", Workstation.Parser), b: v.Get("Building", Building.Parser)))
        .Where(x => x.ws is not null || x.b is not null)
        .Select(x =>
        {
            var p = x.v.Get("Position", Position.Parser);
            var kind = x.ws is not null ? $"工作站#{x.ws.Type}" : $"建筑#{x.b!.Kind}";
            return $"{kind} @({p?.X},{p?.Y})";
        })
        .ToList();
    Console.WriteLine("[工作站/建筑] " + (stations.Count == 0 ? "无" : string.Join(", ", stations)));

    var n = 0;
    foreach (var (id, view) in client.World.Entities)
    {
        if (n++ >= 8) break;
        var pos = view.Get("Position", Position.Parser);
        var hp = view.Get("Health", Health.Parser);
        var player = view.Get("Player", Player.Parser);
        var workable = view.Get("Workable", Workable.Parser);
        Console.WriteLine(
            $"  #{id} @({pos?.X},{pos?.Y})" +
            $" hp={hp?.Cur}/{hp?.Max}" +
            (player is not null ? " [玩家]" : "") +
            (workable is not null ? $" [可采集 kind={workable.Kind}]" : ""));
    }

    Console.WriteLine("阶段 0 冒烟测试通过：协议层已通。");

    // 验证增量合并：跑 2 秒增量后，自己（玩家）的组件不能被冲掉（Player/Health 应还在）
    await Task.Delay(2000);
    if (client.World.Entities.TryGetValue(info.EntityId, out var own))
    {
        var hasPlayer = own.Get("Player", Player.Parser) is not null;
        var hasHealth = own.Get("Health", Health.Parser) is not null;
        var hasPos = own.Get("Position", Starve.Game.V1.Position.Parser) is not null;
        Console.WriteLine(
            $"[增量合并] entity={info.EntityId} Player={hasPlayer} Health={hasHealth} Position={hasPos} " +
            $"组件数={own.Components.Count}");
        if (!hasPlayer || !hasPos) Environment.ExitCode = 1;
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[失败] {ex.Message}");
    Environment.ExitCode = 1;
}
