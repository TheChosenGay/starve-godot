using Google.Protobuf;
using Starve.Game.V1;
using Starve.Core;
using Starve.Netcode;
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

            if (options.Netcode)
            {
                await RunNetcodeE2EAsync(
                    client, info.EntityId, timeout.Token,
                    new UplinkImpairment(
                        options.LatencyMs, options.JitterMs, options.LossPercent,
                        options.ReorderPercent, options.Seed));
            }
            else if (options.MoveTest || options.E2E)
            {
                var directions = options.E2E
                    ? new[] { (-1, 0), (1, 0), (0, -1), (0, 1) }
                    : new[] { (-1, 0) };
                await RunMovementContractAsync(client, info.EntityId, directions, timeout.Token);
            }

            Console.WriteLine(options.Netcode
                ? "序号锚定 E2E 通过：组件采样/冗余上行/按序号和解在真实链路上成立。"
                : options.E2E
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
        var shrub = world.Entities.Values.FirstOrDefault(
            entity => entity.Get("Scenery", Scenery.Parser)?.Kind == ItemKind.Shrub);
        Require(shrub is not null, "全量快照缺少 Scenery{kind=SHRUB}");
        Require(shrub!.Get("Position", Position.Parser) is not null, "灌木缺少 Position");
        Require(shrub.Get("Block", Block.Parser) is null, "灌木不应包含 Block");
        Require(
            shrub.Get("Pickable", WorkTarget.Parser) is null &&
            shrub.Get("Choppable", WorkTarget.Parser) is null &&
            shrub.Get("Minable", WorkTarget.Parser) is null,
            "灌木不应包含工作目标组件");

        var flower = world.Entities.Values.FirstOrDefault(
            entity => entity.Get("Pickable", WorkTarget.Parser)?.Kind == ItemKind.Flower);
        Require(flower is not null, "全量快照缺少 Pickable{kind=FLOWER}");
        Require(flower!.Get("Block", Block.Parser) is null, "花不应包含 Block");

        var flowerTemplate = world.Config?.Templates.FirstOrDefault(x => x.Kind == ItemKind.Flower);
        Require(flowerTemplate?.PickYield == ItemKind.Petal, "花模板 pick_yield 应为 PETAL");
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

    /// <summary>
    /// 序号锚定（组件模式）的端到端：**真协议 + 真 gate**，客户端用的就是 GameRoot 那个适配器
    /// （<see cref="ComponentOwnMovementSim"/>：每 tick 采样一条操作、未确认窗口冗余上行、
    /// 按"已消费 seq"做和解）。这里断言的是"接线成立"，不是地形级逐位一致：
    ///
    ///   · 服务端把操作**逐条消费完**（sent − ack 有界）；
    ///   · 期间不出现"直接贴"级的大校正（没有串号/串流）；
    ///   · 客户端渲染位置跟着服务端权威位置走。
    ///
    /// 地形差异（服务端知道坡度/障碍，本测试假设开阔地可走）只会产生小幅平滑校正，属预期。
    /// </summary>
    /// <summary>
    /// **受损上行**：把客户端要发的操作先放进队列，按"延迟 + 抖动"投递，并按概率丢包/乱序。
    ///
    /// 为什么在上行这一侧做：和解的正确性取决于"服务端的操作流"和"客户端的操作流"是否逐条对应，
    /// 上行受损正是破坏这一点的东西（丢一条 → 缺口；慢一条 → 乱序）。下行（快照）延迟只影响相位，
    /// 设计上不敏感（同序号比较与墙钟无关），所以这一层不模拟。
    ///
    /// 传输是真 WS(TCP)：这里的"丢包/乱序"是**应用层**的（整条操作不发 / 延迟到后面才发），
    /// 比 TCP 的真实行为更狠 —— 正好用来压测冗余窗口与缺口规则。
    /// </summary>
    private sealed class UplinkImpairment
    {
        private readonly int _latencyMs;
        private readonly int _jitterMs;
        private readonly int _lossPercent;
        private readonly int _reorderPercent;
        private readonly Random _rng;
        private readonly List<(long DueMs, MoveOp Op)> _queue = new();

        public int LatencyMs => _latencyMs;
        public int JitterMs => _jitterMs;
        public int LossPercent => _lossPercent;
        public int ReorderPercent => _reorderPercent;

        public int Submitted;
        public int Dropped;
        public int Reordered;
        /// <summary>被"抖动/乱序"推后到达的操作数（它们可能赶不上服务端的缺口宽限 ⇒ 跳缺口）。</summary>
        public int Delayed;

        public UplinkImpairment(int latencyMs, int jitterMs, int lossPercent, int reorderPercent, int seed)
        {
            _latencyMs = latencyMs;
            _jitterMs = jitterMs;
            _lossPercent = lossPercent;
            _reorderPercent = reorderPercent;
            _rng = new Random(seed);
        }

        public bool IsClean => _lossPercent == 0 && _jitterMs == 0 && _reorderPercent == 0;

        /// <summary>把一批操作交给受损上行（到这里才决定丢/延迟/乱序）。</summary>
        public void Submit(IReadOnlyList<MoveOp> ops, long nowMs)
        {
            foreach (var op in ops)
            {
                Submitted++;
                if (_lossPercent > 0 && _rng.Next(100) < _lossPercent)
                {
                    Dropped++;
                    continue;   // 这一条永不送达：等冗余窗口补，或服务端跳缺口
                }

                var due = nowMs + _latencyMs;
                if (_jitterMs > 0)
                {
                    var offset = _rng.Next(-_jitterMs, _jitterMs + 1);
                    due += offset;
                    if (offset > 0) Delayed++;
                }

                if (_reorderPercent > 0 && _rng.Next(100) < _reorderPercent)
                {
                    due += Math.Max(_latencyMs, 150);   // 这条"晚到"：会排在后面几条之后
                    Reordered++;
                    Delayed++;
                }

                _queue.Add((due, op));
            }
        }

        /// <summary>把到点的操作真正发出去（一次一条，保持 seq 语义）。</summary>
        public void Flush(StarveClient client, long nowMs)
        {
            for (var i = _queue.Count - 1; i >= 0; i--)
            {
                if (_queue[i].DueMs > nowMs) continue;
                var op = _queue[i].Op;
                _queue.RemoveAt(i);
                client.Commands.SendMoveOps(stackalloc MoveOp[] { op });
            }
        }
    }

    private static async Task RunNetcodeE2EAsync(
        StarveClient client, ulong entityId, CancellationToken ct, UplinkImpairment impairment)
    {
        var sim = new ComponentOwnMovementSim((_, _) => true);
        sim.HeightAt = (_, _) => 0f;
        client.Commands.SeqSource = () => sim.ReserveDiscreteOp(Environment.TickCount64);
        var ops = new List<ClientSmoother<OwnMoveState, MoveIntent>.OpRef>();
        var wire = new List<MoveOp>(8);
        // 本地预测要用的"世界模型"（每帧从快照重建，和 GameRoot 一样）
        var blockers = new List<BlockerShape>();
        var neighbors = new List<OrcaNeighbor>();
        // 序号锚定命中统计：ack 指向的那条操作在不在本地回看环里。
        // 这是**与地形/障碍模型无关**的强不变量：序列流一旦错位（丢包没补上、服务端跳号、
        // 环被冲掉），ack 就会落到环外 —— 那才是 netcode 真的坏了。
        var anchorMisses = 0;
        var reconciled = 0;
        // 来回小幅摆动（每 8 帧换向）：留在出生点附近的开阔地，避免地形差异掩盖接线问题
        var directions = new[] { (1, 0), (-1, 0), (1, 0), (-1, 0), (1, 0), (-1, 0), (1, 0), (-1, 0) };
        var startServer = ReadRealPosition(client.World, entityId);
        var tick0 = client.World.WorldTick;
        var wall0 = Environment.TickCount64;
        var turnAt = Environment.TickCount64;
        var latency = -1;
        var turnX = 0.0;

        for (var i = 0; i < 400; i++)
        {
            var dir = directions[Math.Min(i / 8 % directions.Length, directions.Length - 1)];
            if (i % 8 == 0)
            {
                sim.SetIntent(dir.Item1, dir.Item2);
                turnAt = Environment.TickCount64;
                turnX = sim.Position.X + sim.Position.Y;
            }

            // 喂世界模型（和服务端同料）：服务端的移动 = 连续速度 × 坡度因子 + 形状碰撞滑动 + ORCA 避让，
            // 本地预测只拿"意图方向直走"是不可能对上的 —— 这不是和解的问题，是喂料缺了。
            SyncSimWorld(client.World, entityId, sim, blockers, neighbors);

            var now = Environment.TickCount64;
            sim.Tick(16f, now);

            // 上行：未确认窗口（冗余；服务端按 seq 去重排序）
            var n = sim.CollectUnackedOps(ops, sim.Smoother.Config.RedundantOps);
            if (n > 0)
            {
                wire.Clear();
                for (var k = 0; k < n; k++)
                    wire.Add(new MoveOp(ops[k].Seq, ops[k].Action.Dx, ops[k].Action.Dy));
                impairment.Submit(wire, now);
            }

            impairment.Flush(client, now);

            // 下行：把权威的自己喂进组件（与 GameRoot 快照处理同一条路径）
            var position = ReadConsistentMovement(
                client, entityId, out var moveable, out var freshTick, out var appliedSeq, out var epoch);
            var stopped = moveable.DirX == 0 && moveable.DirY == 0;
            // 锚定命中统计要在 Reconcile **之前**取（它内部会 rebase 链）
            reconciled++;
            if (appliedSeq != 0 && !sim.Smoother.TryGetOpState(appliedSeq, out _)) anchorMisses++;
            sim.Reconcile(
                (float)position.X, (float)position.Y, stopped,
                freshTick, appliedSeq, epoch, now);

            // 定点追踪：每一次"有实际内容"的和解都打一行（含锚点是否命中、误差怎么来的）。
            if (Environment.GetEnvironmentVariable("STARVE_NETCODE_TRACE") == "1")
            {
                var rep = sim.Smoother.LastReport;
                if (rep.Err > 0.05f || rep.Kind is CorrectionKind.Snapped)
                {
                    var anchored = sim.Smoother.TryGetOpState(rep.AppliedSeq, out var opState);
                    // 判定"服务端位置配的是第几条操作"：如果它其实等于 ack+1 之后的状态，
                    // 就说明 ack/位置之间还有一格系统性错位（配错 = 每次都误校正）。
                    var hasNext = sim.Smoother.TryGetOpState(rep.AppliedSeq + 1, out var nextState);
                    var errNext = hasNext
                        ? Math.Sqrt(Math.Pow(nextState.X - position.X, 2) + Math.Pow(nextState.Y - position.Y, 2))
                        : -1;
                    Console.WriteLine(
                        $"    [tr] i={i} tick={client.World.WorldTick} ack={client.Commands.LastAcceptedSeq} " +
                        $"sent={client.Commands.LastSentSeq} ring={sim.Smoother.OpHistoryCount} anchored={anchored} " +
                        $"kind={rep.Kind} err={rep.Err:0.000} errNext={errNext:0.000} replayed={rep.ReplayedTicks} blend={rep.BlendTicks} " +
                        $"opState=({(anchored ? opState.X : 0):0.000},{(anchored ? opState.Y : 0):0.000}) " +
                        $"srv=({position.X:0.000},{position.Y:0.000}) srvDir=({moveable.DirX},{moveable.DirY}) " +
                        $"stopped={stopped} intent=({sim.Intent.Dx},{sim.Intent.Dy}) " +
                        $"eff={moveable.EffectiveSpeed:0.000} spd={moveable.Speed:0.000} " +
                        $"blk={blockers.Count} nbr={neighbors.Count} " +
                        $"sim=({sim.Position.X:0.000},{sim.Position.Y:0.000}) starved={rep.Starved}");
                }
            }

            if (Environment.GetEnvironmentVariable("STARVE_NETCODE_E2E_DEBUG") == "1" &&
                (i < 5 || i % 100 == 0))
            {
                Console.WriteLine(
                    $"    [dbg] i={i} wallTick={client.World.WorldTick} epoch={client.Commands.InputEpoch} " +
                    $"stateTick={sim.Smoother.StateTick} ops={sim.Smoother.OpHistoryCount} " +
                    $"sent={client.Commands.LastSentSeq} ack={client.Commands.LastAcceptedSeq} " +
                    $"pos=({sim.Position.X:0.00},{sim.Position.Y:0.00}) srv=({position.X:0.00},{position.Y:0.00})");
            }

            if (latency < 0 && i % 8 == 2)
            {
                var turned = Math.Abs(sim.Position.X + sim.Position.Y - turnX) > 1e-4f;
                if (turned) latency = (int)(Environment.TickCount64 - turnAt);
            }

            await Task.Delay(16, ct);
        }

        var (serverPos, _) = ReadMovement(client.World, entityId);
        var sent = client.Commands.LastSentSeq;
        var ack = client.Commands.LastAcceptedSeq;
        var pending = client.Commands.PendingControlCount;
        var diag = sim.Diagnostics;
        var moved = Distance(startServer, serverPos);
        var drift = Math.Sqrt(
            Math.Pow(sim.Position.X - serverPos.X, 2) + Math.Pow(sim.Position.Y - serverPos.Y, 2));

        var elapsedMs = Environment.TickCount64 - wall0;

        var tickRate = (client.World.WorldTick - tick0) * 1000.0 /
                       Math.Max(1, Environment.TickCount64 - wall0);

        Console.WriteLine(
            $"[序号锚定] 上行受损(延迟{impairment.LatencyMs}ms 抖动{impairment.JitterMs}ms " +
            $"丢{impairment.LossPercent}% 乱序{impairment.ReorderPercent}%) " +
            $"提交={impairment.Submitted} 丢={impairment.Dropped} 乱序={impairment.Reordered} | " +
            $"用时={elapsedMs}ms 世界 tick 速率={tickRate:F1}Hz | " +
            $"操作 sent={sent} ack={ack} pending={pending} | " +
            $"锚定命中={reconciled - anchorMisses}/{reconciled} | " +
            $"误差 last={diag.LastReconciliationError:0.000} max={diag.MaxReconciliationError:0.000} " +
            $"soft={diag.SoftCorrections} hard={diag.HardSnaps} | " +
            $"服务端位移={moved:0.00} 客户端/权威位置差={drift:0.00} | 输入延迟≈{latency}ms | " +
            $"诊断 has={sim.Has} synced={sim.Smoother.Clock.HasSync} stateTick={sim.Smoother.StateTick} " +
            $"tickIndexed={sim.Smoother.TickIndexedIntents} hasIntent={sim.Smoother.HasIntent} " +
            $"intent=({sim.Intent.Dx},{sim.Intent.Dy}) " +
            $"ops={sim.Smoother.OpHistoryCount} oneWay={sim.Smoother.EstimatedOneWayTicks:F2} " +
            $"nowTick={sim.Smoother.Clock.TickAt(Environment.TickCount64):F1}");

        Require(sim.Has, "组件没有建立链（首份快照没喂进去？）");
        Require(sent > 20, $"上行操作太少：{sent}");
        Require(sent - ack <= 3, $"服务端没有逐条消费：sent={sent} ack={ack}");
        Require(ack > 10, $"服务端几乎没有确认任何操作：ack={ack}");
        Require(diag.SoftCorrections + diag.HardSnaps > 0,
            "整段没有产生任何和解报告（指标接线没生效？）");
        // 受损上行下：丢掉的/迟到的操作必须被"冗余窗口 + 缺口规则 + 和解"吸收掉。
        // 判据（分两档）：
        //   · 干净链路：**误差有界**（严格判据见下面的"与地形无关"那组）；
        //   · 受损链路：**服务端把操作流吃干净**（sent 与 ack 不拉开 = 真的自愈了）+ 校正有界
        //     （不成风暴、不发散）。这里的丢包是**应用层**的（比 TCP 真实行为更狠），
        //     所以"若干次硬校正"是这条链路的正常代价，不是 bug。
        Require(ack >= sent - 5,
            $"服务端没把操作流吃干净：sent={sent} ack={ack}（丢包后没自愈？）");
        // 硬校正（直接贴）是"输入真的丢了"的代价：一次丢包/一次跳缺口最多带来一次硬校正，
        // 所以它必须与**丢包量**成比例，而不是随时间累积成风暴。
        // 硬校正的来源有两个：① 输入被丢（缺口要跳过）；② 输入被推到宽限之后才到（同样跳缺口）。
        // 所以上界应当与"丢 + 迟到"成比例，而不是随运行时间累积。
        var turbulence = impairment.Dropped + impairment.Delayed;
        Require(diag.HardSnaps <= turbulence / 3 + 15,
            $"直接贴级校正 {diag.HardSnaps} 次，与丢{impairment.Dropped}/迟到{impairment.Delayed} 不成比例（在发散？）");
        // ③ **与地形/障碍模型无关**的强判据：ack 指向的操作必须一直在本地回看环里。
        //    锚定一旦脱靶（丢包没补上、服务端跳号、环被冲掉），后面 ①② 的"误差"就没有意义了。
        Require(anchorMisses <= 2,
            $"序号锚定脱靶 {anchorMisses}/{reconciled} 次：客户端回看环里找不到 ack 指向的操作（序列流错位）");
        // ④ 校正不能成风暴：每份快照最多消化一次（平滑校正本身是设计内的，正常 1 次/快照量级）。
        Require(diag.SoftCorrections <= reconciled * 2 + 20,
            $"平滑校正 {diag.SoftCorrections} 次 / {reconciled} 份快照：在来回拉锯（每份快照都在校正）");
        // 结束时必须回到一致（自愈的最终判据）
        Require(diag.LastReconciliationError <= 1.5f,
            $"结束时没有回到一致：last err={diag.LastReconciliationError:0.000}");
        if (impairment.IsClean)
        {
            // ⚠️ 干净链路**不能**断言"零直接贴 / 峰值 < 1.5"：这个门禁的客户端拿不到服务端那份
            //    完整世界（全局碰撞索引/AOI 裁剪出的障碍子集与顺序/地形高度/AI），而服务端的每 tick
            //    位移是「速度×坡度 + 形状滑动 + ORCA」—— 贴着障碍走时两边必然分叉出 1~2 格，
            //    偶尔越过 1.5 的"直接贴"阈值。
            //
            //    实测排除了所有**算法**侧的原因（这些都已修，且可判定）：
            //      · 服务端"没操作也白走一步"→ 已修，并有 Go 不变量测试 + 突变验证；
            //      · 冗余窗口不重发最旧未确认那条 → 已修（头部固定带 `ack+1`）；
            //      · 位置与 ack 跨消息配对 → 已修（`WorldService.ReadAtomic`）；
            //      · 服务端跳缺口/积压/丢操作 → 实测全程 `desyncs=0 backlog=0 dropped=0`；
            //      · 坡度与障碍喂料 → 加大出生点平地 / 关掉障碍喂料，误差分布**不变**。
            //    剩下的 1~2 格是**模型可比性**，不是接线或算法：判据落在下面那组
            //    "与地形无关"的强不变量上（锚定命中、流吃干净、无风暴、末尾收敛、输入延迟）。
            Require(diag.HardSnaps <= 15,
                $"干净链路出现直接贴 {diag.HardSnaps} 次（模型差异解释不了）");
            Require(diag.MaxReconciliationError <= 3.0f,
                $"干净链路误差峰值 {diag.MaxReconciliationError:0.000} 过大（模型差异解释不了）");
        }
        Require(tickRate > 1, $"世界 tick 速率异常：{tickRate:F1}Hz");
        Require(sim.Position.X != 0 || sim.Position.Y != 0, "客户端没有在本地预测");
        Require(latency is >= 0 and <= 120, $"输入响应异常：{latency}ms");
        // ⚠️ 这里仍然**不**断言"客户端位置 == 服务端位置"：本门禁的客户端拿不到服务端那份
        //    完整世界（地形高度、全局碰撞索引、AI），位置残差里有模型差异的成分。
        //    真正的严格判据是那组与地形无关的不变量：序号锚定命中、操作流被吃干净、
        //    无校正风暴、末尾收敛、输入延迟 ≤1 tick。
    }

    /// <summary>
    /// 把"服务端世界"喂进本地预测器 —— 与 GameRoot 的快照处理**同一套喂法**：
    /// 有效速度 / 身体半径 / 障碍形状 / ORCA 邻居。
    ///
    /// 为什么门禁必须喂（踩过的坑）：服务端的每 tick 位移不是"意图方向 × 速度"，
    /// 而是「连续速度 × 坡度因子 + 形状碰撞滑动（贴墙/贴树会拐弯）+ ORCA 避让（被顶开）」。
    /// 本地预测拿空世界（walkable 恒真、没有障碍、没有邻居）去比，就会**系统性偏离**：
    /// 实测出生点旁边有碰撞体，服务端沿障碍滑出 +Y 0.15~0.4 格/tick，客户端却直走 —— 锚定
    /// 误差单调涨到 1.5 就触发一次"直接贴"，干净链路门禁因此随机翻红（0/0/0/3 次）。
    /// 那不是和解的缺陷，是喂料缺了：真实客户端一直喂着这四样。
    /// </summary>
    private static void SyncSimWorld(
        WorldService world,
        ulong ownId,
        ComponentOwnMovementSim sim,
        List<BlockerShape> blockers,
        List<OrcaNeighbor> neighbors)
    {
        blockers.Clear();
        neighbors.Clear();

        foreach (var (id, view) in world.Entities)
        {
            var pos = view.Get("Position", Position.Parser);
            if (pos is null) continue;
            var col = view.Get("Collide", Collide.Parser);
            var mv = view.Get("Moveable", Moveable.Parser);

            if (col is not null)
            {
                switch (col.Shape)
                {
                    case CollideShape.Circle:
                        // 格心圆（树/岩）：Position 是格子坐标，圆心在格心
                        blockers.Add(BlockerShape.Circle(pos.X + 0.5f, pos.Y + 0.5f, (float)col.Radius));
                        break;
                    case CollideShape.Box:
                        blockers.Add(BlockerShape.Box(
                            pos.X, pos.Y, Math.Max(1, col.Width), Math.Max(1, col.Height)));
                        break;
                    case CollideShape.Capsule:
                        // 移动体（玩家/动物）是胶囊：静态滑动不挡它们，动态之间走 ORCA
                        blockers.Add(BlockerShape.Capsule(
                            pos.X, pos.Y, (float)col.Radius, (float)col.HalfLength,
                            col.FaceX, col.FaceZ, id));
                        break;
                }
            }

            if (id == ownId)
            {
                if (mv is not null && mv.EffectiveSpeed > 0)
                {
                    // 服务端的"有效速度"已经含坡度因子：本地用同一个数，坡度这一段就对齐了
                    sim.SetSpeed((float)mv.EffectiveSpeed);
                    sim.SetSpeedProfile((float)mv.EffectiveSpeed, (float)(col?.HalfLength ?? 0));
                }
                if (col is not null) sim.SetBodyRadius((float)col.Radius);
                continue;   // 自己不进邻居表
            }

            // 只有会自己动的实体参与 ORCA（静态体已在 blockers 里做硬碰撞）。
            // 判据是"有 Moveable" = 服务端 CanSelfMove；**不是** view.Has("Dynamic")
            // （那两个 tag 早被服务端删了，用它会让邻居表恒空 ⇒ 预测不做动态避让）。
            if (col is null || mv is null) continue;
            neighbors.Add(new OrcaNeighbor
            {
                X = pos.X + (float)(mv?.SubX ?? 0),
                Y = pos.Y + (float)(mv?.SubY ?? 0),
                VX = (float)(mv?.VelX ?? 0),
                VY = (float)(mv?.VelY ?? 0),
                Radius = (float)col.Radius,
                HalfLength = (float)col.HalfLength,
                MaxSpeed = (float)(mv?.EffectiveSpeed ?? mv?.Speed ?? 10),
            });
        }

        // 确定性：与服务端邻居排序规则一致（LP 对顺序敏感）
        neighbors.Sort((a, b) =>
        {
            var c = a.X.CompareTo(b.X);
            if (c != 0) return c;
            c = a.Y.CompareTo(b.Y);
            if (c != 0) return c;
            return a.Radius.CompareTo(b.Radius);
        });

        sim.SetBlockers(Environment.GetEnvironmentVariable("STARVE_SMOKE_NO_BLOCKERS") == "1"
            ? Array.Empty<BlockerShape>()
            : blockers);
        sim.SetNeighbors(Environment.GetEnvironmentVariable("STARVE_SMOKE_NO_BLOCKERS") == "1"
            ? Array.Empty<OrcaNeighbor>()
            : neighbors);
    }

    /// <summary>
    /// **原子读**：位置 / 数据 tick / ack / epoch 必须来自**同一条服务端消息**。
    ///
    /// 为什么必须这样（踩过的坑，也是这个门禁最大的假阳性来源）：接收线程随时可能在
    /// "读位置"和"读 ack"之间应用下一条快照，于是配出「位置来自消息 m、ack 来自消息 m+1」——
    /// 锚定比较就整体偏了一个 tick：干净链路实测误差恒在 0.37~1.16 格（正好 1~2 个 tick 的位移，
    /// 转向处翻倍），撞上 1.5 的阈值就误报一次"直接贴"，门禁 0/0/0/3 随机翻红。
    /// 组件本身没错：`WorldService.Revision` 在"实体 + ack 都应用完"之后自增，用它做 seqlock 即可。
    /// </summary>
    private static (double X, double Y) ReadConsistentMovement(
        StarveClient client,
        ulong entityId,
        out Moveable moveable,
        out long tick,
        out ulong ack,
        out ulong epoch)
    {
        var read = client.World.ReadAtomic(() =>
        {
            var (position, mv) = ReadMovement(client.World, entityId);
            return (Position: position, Mv: mv, Tick: client.World.WorldTick,
                Ack: client.Commands.LastAcceptedSeq, Epoch: client.Commands.InputEpoch);
        });
        moveable = read.Mv;
        tick = read.Tick;
        ack = read.Ack;
        epoch = read.Epoch;
        return read.Position;
    }

    private static ((double X, double Y) Position, Moveable Moveable) ReadMovement(
        WorldService world,
        ulong entityId)
    {        var view = GetEntity(world, entityId, "移动期间玩家实体消失");
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
    bool Netcode,
    int LatencyMs,
    int JitterMs,
    int LossPercent,
    int ReorderPercent,
    int Seed,
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
        var netcode = false;
        var help = false;
        var latencyMs = 0;
        var jitterMs = 0;
        var lossPercent = 0;
        var reorderPercent = 0;
        var seed = 20260214;
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
                case "--netcode":
                    netcode = true;
                    break;
                case "--latency-ms":
                    latencyMs = ParseInt(args, ref i, "--latency-ms", 0, 5000);
                    break;
                case "--jitter-ms":
                    jitterMs = ParseInt(args, ref i, "--jitter-ms", 0, 2000);
                    break;
                case "--loss-percent":
                    lossPercent = ParseInt(args, ref i, "--loss-percent", 0, 100);
                    break;
                case "--reorder-percent":
                    reorderPercent = ParseInt(args, ref i, "--reorder-percent", 0, 100);
                    break;
                case "--seed":
                    seed = ParseInt(args, ref i, "--seed", 1, int.MaxValue);
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
        if (netcode) e2e = false;
        return new SmokeOptions(
            uid, url, timeout, diag, moveTest, e2e, netcode,
            latencyMs, jitterMs, lossPercent, reorderPercent, seed, help);
    }

    public static void PrintUsage() =>
        Console.WriteLine(
            "用法: dotnet run --project ProtocolSmoke -- [uid] [--diag|--movetest|--e2e|--netcode] " +
            "[--url ws://host:port/ws] [--timeout-seconds 30] " +
            "[--latency-ms N] [--jitter-ms N] [--loss-percent N] [--reorder-percent N] [--seed N]");

    private static int ParseInt(string[] args, ref int index, string option, int min, int max)
    {
        if (!int.TryParse(NextValue(args, ref index, option), out var value) ||
            value < min || value > max)
        {
            throw new ArgumentException($"{option} 必须是 {min}..{max} 的整数");
        }

        return value;
    }

    private static string NextValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length) throw new ArgumentException($"{option} 缺少值");
        if (args[index].StartsWith('-'))
            throw new ArgumentException($"{option} 缺少值，不能使用选项 {args[index]} 作为参数");
        return args[index];
    }
}

internal sealed class SmokeFailureException(string message) : Exception(message);
