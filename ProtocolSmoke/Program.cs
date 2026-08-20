using Google.Protobuf;
using Starve.Game.V1;
using Starve.Protocol;
using Starve.Protocol.Pomelo;
using Starve.Protocol.World;
using Starve.Proto.V1;

return await SmokeRunner.RunAsync(args);

internal static class SmokeRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        SmokeOptions options;
        try
        {
            options = SmokeOptions.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[参数错误] {ex.Message}");
            SmokeOptions.PrintUsage();
            return 2;
        }

        if (options.Help)
        {
            SmokeOptions.PrintUsage();
            return 0;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        using var client = new StarveClient();
        var worldEvents = 0;
        var outcomes = 0;
        var impacts = 0;
        var healthChanges = 0;
        client.World.WorldEventReceived += _ => worldEvents++;
        client.World.ActionOutcomeReceived += _ => outcomes++;
        client.World.CombatImpactReceived += (_, _) => impacts++;
        client.World.HealthChangedReceived += (_, _) => healthChanges++;
        var connected = false;
        try
        {
            var info = await client.ConnectAsync(options.Url, DevTokens.Mint(options.Uid), timeout.Token);
            connected = true;
            Console.WriteLine(
                $"[登录成功] protocol={client.Transport.ProtocolVersion} " +
                $"uid={info.UserId} entity={info.EntityId} epoch={info.InputEpoch}");
            Require(client.World.InputEpoch == info.InputEpoch, "全量快照 input_epoch 与登录不一致");
            ValidateFullSnapshot(client.World, info.EntityId);
            ValidateActionOutcomeRoute();
            ValidateAutomateModes();
            PrintWorldSummary(client.World, info.EntityId);

            if (options.Diag)
            {
                await RunDiagnosticsAsync(client, info.EntityId, timeout.Token);
                return 0;
            }

            var initialRevision = client.World.Revision;
            await WaitForIncrementalAsync(client.World, initialRevision, timeout.Token);
            ValidateMergedOwnEntity(client.World, info.EntityId);
            Console.WriteLine(
                $"[世界事件] total={worldEvents} outcome={outcomes} " +
                $"impact={impacts} health_changed={healthChanges}");

            if (options.MoveTest || options.E2E)
            {
                var directions = options.E2E
                    ? new[] { (-1, 0), (1, 0), (0, -1), (0, 1) }
                    : new[] { (-1, 0) };
                await RunMovementContractAsync(client, info.EntityId, directions, timeout.Token);
            }

            Console.WriteLine(options.E2E
                ? "P1.1 E2E 通过：协议能力、epoch、tick、ACK 和移动契约均有效。"
                : "协议冒烟测试通过。");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine($"[失败] 超过 {options.TimeoutSeconds} 秒，测试已取消。");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[失败] {ex.Message}");
            return 1;
        }
        finally
        {
            if (connected)
            {
                client.Commands.Move(0, 0);
            }
        }
    }

    private static void ValidateFullSnapshot(WorldService world, ulong entityId)
    {
        Require(world.Count > 0, "全量快照为空");
        var own = GetEntity(world, entityId, "全量快照缺少登录玩家");
        Require(own.Get("Player", Player.Parser) is not null, "全量快照缺少 Player 组件");
        Require(own.Get("Position", Position.Parser) is not null, "全量快照缺少 Position 组件");
        Require(own.Get("Moveable", Moveable.Parser) is not null, "全量快照缺少 Moveable 组件");
        foreach (var entity in world.Entities.Values)
        {
            if (entity.Get("Workstation", Workstation.Parser) is not null)
                Require(entity.Get("Block", Block.Parser) is not null, "工作站快照缺少 Block 组件");
        }
        var parsedActions = world.Entities.Values.Count(
            entity => entity.Get("ActionState", ActionState.Parser) is not null);
        Console.WriteLine(
            $"[全量快照] 实体数={world.Count} tick={world.WorldTick} " +
            $"玩家组件={own.Components.Count} ActionState={parsedActions}");
    }

    private static void ValidateActionOutcomeRoute()
    {
        var world = new WorldService();
        ActionOutcome? parsed = null;
        world.ActionOutcomeReceived += outcome => parsed = outcome;
        world.HandleMessage(new PomeloMessage
        {
            Type = MsgType.Push,
            Route = Routes.ActionOutcome,
            Data = new ActionOutcome
            {
                EntityId = 7,
                ActionId = 9,
                Kind = ActionKind.Attack,
                Result = ActionOutcomeResult.Canceled,
                Reason = ActionOutcomeReason.Moved,
                Tick = 11,
            }.ToByteArray(),
        });
        Require(
            parsed is { EntityId: 7, ActionId: 9, Result: ActionOutcomeResult.Canceled },
            "ActionOutcome route 无法解析");
        Console.WriteLine("[动作结果] world.action.outcome 路由解析通过");
    }

    private static void ValidateAutomateModes()
    {
        foreach (var mode in new[] { AutomateMode.Any, AutomateMode.AttackOnly })
        {
            var bytes = new PlayerAutomate { Mode = mode }.ToByteArray();
            Require(PlayerAutomate.Parser.ParseFrom(bytes).Mode == mode, $"AutomateMode {mode} 无法解析");
        }
        Console.WriteLine("[自动行为] ANY/ATTACK_ONLY protobuf 解析通过");
    }

    private static async Task WaitForIncrementalAsync(
        WorldService world,
        int initialRevision,
        CancellationToken ct)
    {
        await WaitUntilAsync(
            () => world.Revision != initialRevision,
            TimeSpan.FromSeconds(5),
            "未收到推进世界 tick 的增量快照",
            ct);
        Console.WriteLine($"[增量快照] tick={world.WorldTick} last_seq={world.LastAcceptedSeq} revision={world.Revision}");
    }

    private static void ValidateMergedOwnEntity(WorldService world, ulong entityId)
    {
        var own = GetEntity(world, entityId, "增量后登录玩家消失");
        var hasPlayer = own.Get("Player", Player.Parser) is not null;
        var hasHealth = own.Get("Health", Health.Parser) is not null;
        var hasPosition = own.Get("Position", Position.Parser) is not null;
        var moveable = own.Get("Moveable", Moveable.Parser)
            ?? throw new SmokeFailureException("增量合并冲掉玩家 Moveable 组件");
        Require(hasPlayer && hasHealth && hasPosition, "增量合并冲掉玩家基础组件");
        ValidateMoveable(moveable);
        Console.WriteLine(
            $"[增量合并] Player={hasPlayer} Health={hasHealth} Position={hasPosition} " +
            $"speed={moveable.Speed:0.##} effective={moveable.EffectiveSpeed:0.##} " +
            $"sub=({moveable.SubX:0.00},{moveable.SubY:0.00})");
    }

    private static async Task RunMovementContractAsync(
        StarveClient client,
        ulong entityId,
        IReadOnlyList<(int Dx, int Dy)> directions,
        CancellationToken ct)
    {
        var start = ReadRealPosition(client.World, entityId);
        var moved = false;
        try
        {
            foreach (var direction in directions)
            {
                var attemptStart = ReadRealPosition(client.World, entityId);
                Console.WriteLine($"[移动测试] 尝试方向 ({direction.Dx},{direction.Dy})");
                for (var i = 0; i < 10; i++)
                {
                    client.Commands.Move(direction.Dx, direction.Dy);
                    await Task.Delay(100, ct);
                    var (position, moveable) = ReadMovement(client.World, entityId);
                    ValidateMoveable(moveable);
                    var directionMatches =
                        moveable.DirX == direction.Dx && moveable.DirY == direction.Dy;
                    Console.WriteLine(
                        $"  t={(i + 1) * 100}ms pos=({position.X:0.00},{position.Y:0.00}) " +
                        $"sub=({moveable.SubX:0.00},{moveable.SubY:0.00}) dir=({moveable.DirX},{moveable.DirY})");
                    if (directionMatches && Distance(attemptStart, position) > 0.05)
                    {
                        moved = true;
                        break;
                    }
                }
                client.Commands.Move(0, 0);
                if (moved) break;
                await WaitForStoppedAsync(client.World, entityId, ct);
            }
        }
        finally
        {
            client.Commands.Move(0, 0);
        }

        Require(
            moved,
            $"服务端未在同一次尝试中确认方向并产生位移，起点=({start.X:0.00},{start.Y:0.00})");
        await WaitForStoppedAsync(client.World, entityId, ct);
        await WaitForInputAckAsync(client, ct);
        Console.WriteLine("[移动契约] 方向、速度、sub 范围、位移和停止确认通过");
    }

    private static async Task WaitForInputAckAsync(StarveClient client, CancellationToken ct)
    {
        var sent = client.Commands.LastSentSeq;
        await WaitUntilAsync(
            () => client.Commands.LastAcceptedSeq >= sent,
            TimeSpan.FromSeconds(3),
            $"输入 ACK 未追上：sent={sent} ack={client.Commands.LastAcceptedSeq}",
            ct);
        Require(client.Commands.InputEpoch == client.World.InputEpoch, "输入 ACK epoch 不一致");
        Console.WriteLine(
            $"[输入确认] epoch={client.Commands.InputEpoch} sent={sent} " +
            $"ack={client.Commands.LastAcceptedSeq} pending={client.Commands.PendingControlCount}");
    }

    private static async Task WaitForStoppedAsync(
        WorldService world,
        ulong entityId,
        CancellationToken ct)
    {
        await WaitUntilAsync(
            () =>
            {
                var (_, moveable) = ReadMovement(world, entityId);
                ValidateMoveable(moveable);
                return moveable.DirX == 0 && moveable.DirY == 0;
            },
            TimeSpan.FromSeconds(3),
            "停止命令未被增量快照确认",
            ct);
    }

    private static void ValidateMoveable(Moveable moveable)
    {
        Require(moveable.Speed > 0, "Moveable.speed 必须大于 0");
        Require(moveable.EffectiveSpeed >= 0, "Moveable.effective_speed 不能为负数");
        Require(moveable.DirX is >= -1 and <= 1 && moveable.DirY is >= -1 and <= 1,
            "Moveable.dir 超出 -1..1");
        Require(moveable.SubX is >= 0 and < 1 && moveable.SubY is >= 0 and < 1,
            $"Moveable.sub 超出 [0,1): ({moveable.SubX},{moveable.SubY})");
    }

    private static ((double X, double Y) Position, Moveable Moveable) ReadMovement(
        WorldService world,
        ulong entityId)
    {
        var view = GetEntity(world, entityId, "移动期间玩家实体消失");
        var position = view.Get("Position", Position.Parser)
            ?? throw new SmokeFailureException("移动增量缺少 Position");
        var moveable = view.Get("Moveable", Moveable.Parser)
            ?? throw new SmokeFailureException("移动增量缺少 Moveable");
        return ((position.X + moveable.SubX, position.Y + moveable.SubY), moveable);
    }

    private static EntityView GetEntity(WorldService world, ulong entityId, string failure) =>
        world.Entities.TryGetValue(entityId, out var view)
            ? view
            : throw new SmokeFailureException(failure);

    private static (double X, double Y) ReadRealPosition(WorldService world, ulong entityId) =>
        ReadMovement(world, entityId).Position;

    private static double Distance((double X, double Y) a, (double X, double Y) b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan timeout,
        string failure,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new SmokeFailureException(failure);
            await Task.Delay(25, ct);
        }
    }

    private static async Task RunDiagnosticsAsync(StarveClient client, ulong entityId, CancellationToken ct)
    {
        var blocks = client.World.Entities.Values
            .Select(v => (b: v.Get("Block", Block.Parser), p: v.Get("Position", Position.Parser)))
            .Where(x => x.b is not null && x.p is not null)
            .Select(x => $"({x.p!.X},{x.p.Y}) {x.b!.Width}x{x.b.Height}")
            .ToList();
        Console.WriteLine("[Block 实体] " +
                          (blocks.Count == 0 ? "无！树/矿未挂 Block" : $"共 {blocks.Count} 个: " + string.Join(", ", blocks.Take(12))));

        var stations = client.World.Entities.Values
            .Select(v => (ws: v.Get("Workstation", Workstation.Parser), p: v.Get("Position", Position.Parser)))
            .Where(x => x.ws is not null && x.p is not null)
            .Select(x => $"类型{x.ws!.Type}@({x.p!.X},{x.p.Y})")
            .ToList();
        Console.WriteLine("[工作站] " + (stations.Count == 0 ? "无" : string.Join(", ", stations)));

        Console.WriteLine("[制作测试] 走向工作台 (62,66) ...");
        for (var steps = 0; steps < 160; steps++)
        {
            var current = client.World.Entities.TryGetValue(entityId, out var view)
                ? view.Get("Position", Position.Parser)
                : null;
            if (current is null || Math.Abs(current.X - 62) + Math.Abs(current.Y - 66) <= 2) break;
            client.Commands.Move(Math.Clamp(62 - current.X, -1, 1), Math.Clamp(66 - current.Y, -1, 1));
            await Task.Delay(100, ct);
        }
        client.Commands.Move(0, 0);
        await Task.Delay(300, ct);
        foreach (var recipeId in new[] { "pickaxe", "axe" })
        {
            var result = await client.Commands.CraftAsync(recipeId, ct);
            var response = result.Response;
            Console.WriteLine(
                $"  craft {recipeId} → " +
                (response is null ? "超时" : response.Started ? "OK started" : $"失败: {response.Message}"));
        }
    }

    private static WorkTarget? WorkTargetOf(EntityView view) =>
        view.Get("Choppable", WorkTarget.Parser)
        ?? view.Get("Minable", WorkTarget.Parser)
        ?? view.Get("Pickable", WorkTarget.Parser);

    private static void PrintWorldSummary(WorldService world, ulong ownId)
    {
        var stations = world.Entities.Values
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
        var blockers = world.Entities.Values.Count(v => v.Get("Block", Block.Parser) is not null);
        Console.WriteLine($"[动态阻挡] Block 实体数={blockers}");
        var own = GetEntity(world, ownId, "玩家实体不存在");
        var ownPos = own.Get("Position", Position.Parser)!;
        var nearby = world.Entities.Values
            .Where(v => v.EntityId != ownId)
            .Select(v => new
            {
                View = v,
                Position = v.Get("Position", Position.Parser),
                Target = WorkTargetOf(v),
            })
            .Where(x => x.Position is not null &&
                        (x.Target is not null || x.View.LootOf() is not null))
            .Select(x => new
            {
                x.View.EntityId,
                Distance = Math.Abs(x.Position!.X - ownPos.X) +
                           Math.Abs(x.Position.Y - ownPos.Y),
                Action = x.View.Get("Pickable", WorkTarget.Parser) is not null ? "pick"
                    : x.View.Get("Choppable", WorkTarget.Parser) is not null ? "chop"
                    : x.View.Get("Minable", WorkTarget.Parser) is not null ? "mine"
                    : "pickup",
                Kind = x.Target?.Kind.ToString() ?? "loot",
            })
            .Where(x => x.Distance <= 8)
            .OrderBy(x => x.Distance)
            .Take(8)
            .ToList();
        Console.WriteLine(
            $"[自动行为诊断] Picker={own.Components.ContainsKey("Picker")} " +
            $"附近目标=" +
            (nearby.Count == 0
                ? "无"
                : string.Join(", ", nearby.Select(
                    x => $"#{x.EntityId}:{x.Action}/{x.Kind}/d{x.Distance}"))));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new SmokeFailureException(message);
    }
}

internal sealed record SmokeOptions(
    string Uid,
    string Url,
    int TimeoutSeconds,
    bool Diag,
    bool MoveTest,
    bool E2E,
    bool Help)
{
    public static SmokeOptions Parse(string[] args)
    {
        var uid = "42";
        var url = Environment.GetEnvironmentVariable("STARVE_GATE_URL") ?? "ws://localhost:8081/ws";
        var timeout = 30;
        var diag = false;
        var moveTest = false;
        var e2e = false;
        var help = false;
        var positionalUidSeen = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--diag":
                    diag = true;
                    break;
                case "--movetest":
                    moveTest = true;
                    break;
                case "--e2e":
                    e2e = true;
                    break;
                case "--url":
                    url = NextValue(args, ref i, "--url");
                    break;
                case "--uid":
                    uid = NextValue(args, ref i, "--uid");
                    positionalUidSeen = true;
                    break;
                case "--timeout-seconds":
                    if (!int.TryParse(NextValue(args, ref i, "--timeout-seconds"), out timeout) || timeout <= 0)
                        throw new ArgumentException("--timeout-seconds 必须是正整数");
                    break;
                case "--help":
                case "-h":
                    help = true;
                    break;
                default:
                    if (args[i].StartsWith('-')) throw new ArgumentException($"未知参数: {args[i]}");
                    if (positionalUidSeen) throw new ArgumentException("只能指定一个 uid");
                    uid = args[i];
                    positionalUidSeen = true;
                    break;
            }
        }

        if (diag && e2e) throw new ArgumentException("--diag 与 --e2e 不可同时使用");
        return new SmokeOptions(uid, url, timeout, diag, moveTest, e2e, help);
    }

    public static void PrintUsage() =>
        Console.WriteLine(
            "用法: dotnet run --project ProtocolSmoke -- [uid] [--diag|--movetest|--e2e] " +
            "[--url ws://host:port/ws] [--timeout-seconds 30]");

    private static string NextValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length) throw new ArgumentException($"{option} 缺少值");
        if (args[index].StartsWith('-'))
            throw new ArgumentException($"{option} 缺少值，不能使用选项 {args[index]} 作为参数");
        return args[index];
    }
}

internal sealed class SmokeFailureException(string message) : Exception(message);
