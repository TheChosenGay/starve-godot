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
        var reactive = view.Get("Choppable", WorkTarget.Parser)
            ?? view.Get("Minable", WorkTarget.Parser)
            ?? view.Get("Pickable", WorkTarget.Parser);
        Console.WriteLine(
            $"  #{id} @({pos?.X},{pos?.Y})" +
            $" hp={hp?.Cur}/{hp?.Max}" +
            (player is not null ? " [玩家]" : "") +
            (reactive is not null ? $" [资源 kind={reactive.Kind} 工作={reactive.WorkLeft}/{reactive.MaxWork}]" : ""));
    }

    Console.WriteLine("阶段 0 冒烟测试通过：协议层已通。");

    // 诊断：树/岩 Block 数量 + 工作台制作实测（--diag）
    if (args.Contains("--diag"))
    {
        var blocks = client.World.Entities.Values
            .Select(v => (v, b: v.Get("Block", Block.Parser), p: v.Get("Position", Position.Parser)))
            .Where(x => x.b is not null && x.p is not null)
            .Select(x => $"({x.p!.X},{x.p.Y}) {x.b!.Width}x{x.b.Height}")
            .ToList();
        Console.WriteLine("[Block 实体] " + (blocks.Count == 0 ? "无！树/矿未挂 Block" : $"共 {blocks.Count} 个: " + string.Join(", ", blocks.Take(12))));

        var stationList = client.World.Entities.Values
            .Select(v => (v, ws: v.Get("Workstation", Workstation.Parser), p: v.Get("Position", Position.Parser)))
            .Where(x => x.ws is not null && x.p is not null)
            .Select(x => $"类型{x.ws!.Type}@({x.p!.X},{x.p.Y})")
            .ToList();
        Console.WriteLine("[工作站] " + (stationList.Count == 0 ? "无" : string.Join(", ", stationList)));

        // 把玩家挪到工作台附近（62,66），然后尝试制作
        Console.WriteLine("[制作测试] 走向工作台 (62,66) ...");
        var steps = 0;
        while (steps < 160)
        {
            var cur = client.World.Entities.TryGetValue(info.EntityId, out var v) ? v.Get("Position", Position.Parser) : null;
            if (cur is null) break;
            if (Math.Abs(cur.X - 62) + Math.Abs(cur.Y - 66) <= 2) break;
            var dx = Math.Clamp(62 - cur.X, -1, 1);
            var dy = Math.Clamp(66 - cur.Y, -1, 1);
            if (dx != 0 || dy != 0) client.Commands.Move(dx, dy);
            await Task.Delay(100);
            steps++;
        }
        client.Commands.Move(0, 0);
        await Task.Delay(300);
        foreach (var rid in new[] { "pickaxe", "axe" })
        {
            var resp = await client.Commands.CraftAsync(rid);
            Console.WriteLine($"  craft {rid} → {(resp is null ? "超时" : resp.Started ? "OK started" : $"失败: {resp.Message}")}");
        }
        return;
    }

    // 移动契约验证：--movetest 向左走 2 秒，观察 Position/sub 推进（负方向回归）。
    if (args.Contains("--movetest"))
    {
        client.Commands.Move(-1, 0);
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(100);
            if (client.World.Entities.TryGetValue(info.EntityId, out var v))
            {
                var p = v.Get("Position", Position.Parser);
                var mv = v.Get("Moveable", Moveable.Parser);
                Console.WriteLine($"  t={i * 100}ms pos=({p?.X},{p?.Y}) sub=({mv?.SubX:0.00},{mv?.SubY:0.00}) dir=({mv?.DirX},{mv?.DirY})");
            }
        }
        client.Commands.Move(0, 0);
        return;
    }

    // 验证增量合并：跑 2 秒增量后，自己（玩家）的组件不能被冲掉（Player/Health 应还在）
    await Task.Delay(2000);
    if (client.World.Entities.TryGetValue(info.EntityId, out var own))
    {
        var hasPlayer = own.Get("Player", Player.Parser) is not null;
        var hasHealth = own.Get("Health", Health.Parser) is not null;
        var hasPos = own.Get("Position", Starve.Game.V1.Position.Parser) is not null;
        var hasChopper = own.Get("Chopper", Capability.Parser) is not null;
        var hasMiner = own.Get("Miner", Capability.Parser) is not null;
        var equip = own.Get("Equip", Equip.Parser);
        var ownPos = own.Get("Position", Starve.Game.V1.Position.Parser);
        var ownMv = own.Get("Moveable", Moveable.Parser);
        var defPct = 0;
        var wear = new List<string>();
        foreach (var (slot, id) in new[] { ("头", equip?.Head ?? 0), ("身", equip?.Body ?? 0) })
        {
            if (id == 0 || !client.World.Entities.TryGetValue(id, out var item)) continue;
            if (item.Get("Defense", Defense.Parser) is not { } d) continue;
            defPct += d.Percent;
            var kind = (int?)item.Get("Equipment", ItemStack.Parser)?.Kind ?? 0;
            wear.Add($"{slot}甲#{kind}");
        }
        Console.WriteLine(
            $"[增量合并] entity={info.EntityId} Player={hasPlayer} Health={hasHealth} Position={hasPos} " +
            $"@({ownPos?.X},{ownPos?.Y}) mv(speed={ownMv?.Speed} dir={ownMv?.DirX},{ownMv?.DirY} sub={ownMv?.SubX:0.00},{ownMv?.SubY:0.00}) " +
            $"Chopper={hasChopper} Miner={hasMiner} Equip.Hand={equip?.Hand} " +
            $"护甲=[{string.Join(" ", wear)}] 防御={defPct}% 组件数={own.Components.Count}");
        if (!hasPlayer || !hasPos) Environment.ExitCode = 1;
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[失败] {ex.Message}");
    Environment.ExitCode = 1;
}
