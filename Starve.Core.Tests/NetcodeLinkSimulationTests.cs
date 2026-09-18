using Starve.Core;
using Starve.Netcode;

namespace Starve.Core.Tests;

/// <summary>
/// 服务端↔客户端链路仿真（**不启动 Godot、不碰网络栈**，纯逻辑 + 真实移动模型）。
///
/// 目的：在没有引擎、没有真实网络的情况下，量化"预测 / 重放 / 回退"到底表现如何。
/// 服务端与客户端跑**同一套移动数学**（<see cref="OwnMovePredictor"/>，
/// 由 golden 向量守契约），差别只在"世界真相"与"延迟"。
///
/// 指标经 <see cref="INetcodeMetrics"/> 出口收集 —— 真实业务就是实现这个接口，
/// 把同样的数据接到自己的日志上。
///
/// 统计口径（都是从 <see cref="CorrectionReport"/> 来的）：
///   · <b>接受率</b> = 预测与权威一致、无需校正的快照占比（Deadzone）
///   · <b>重放</b> = 触发了重放的快照数与平均/最大重放 tick
///   · <b>回退率</b> = 需要"直接贴"的快照占比（大偏差：撞墙/传送/世界真相不一致）
/// </summary>
public sealed class NetcodeLinkSimulationTests
{
    // ── 指标出口：真实业务要实现的接口 ──────────────────────────
    private sealed class Collector : INetcodeMetrics
    {
        public int Snapshots, Stale, Bootstrap, Deadzone, Blended, Snapped, Clamped, Starved;
        public int ReplaySnapshots, ReplayTicksTotal, ReplayTicksMax;
        public double ErrSum, ErrMax;
        public double DriftSum;
        public int DriftSamples;

        public void OnCorrection(in CorrectionReport report)
        {
            Snapshots++;
            switch (report.Kind)
            {
                case CorrectionKind.Stale: Stale++; break;
                case CorrectionKind.Bootstrap: Bootstrap++; break;
                case CorrectionKind.Deadzone: Deadzone++; break;
                case CorrectionKind.Blended: Blended++; break;
                case CorrectionKind.Snapped: Snapped++; break;
            }

            if (report.ReplayedTicks > 0)
            {
                ReplaySnapshots++;
                ReplayTicksTotal += report.ReplayedTicks;
                ReplayTicksMax = Math.Max(ReplayTicksMax, report.ReplayedTicks);
            }

            if (report.ReplayClamped) Clamped++;
            if (report.Starved) Starved++;
            ErrSum += report.Err;
            ErrMax = Math.Max(ErrMax, report.Err);
            // 只有"本区间本地确实推过步"时，速度对比才有意义。
            if (report.PredictedSteps > 0)
            {
                DriftSum += Math.Abs(report.PredictedStepSpeed - report.AuthoritativeStepSpeed);
                DriftSamples++;
            }
        }

        public double AcceptRate => Snapshots == 0 ? 0 : (double)Deadzone / Snapshots;
        public double BlendRate => Snapshots == 0 ? 0 : (double)Blended / Snapshots;
        public double SnapRate => Snapshots == 0 ? 0 : (double)Snapped / Snapshots;
        public double AvgReplay => ReplaySnapshots == 0 ? 0 : (double)ReplayTicksTotal / ReplaySnapshots;
        public double MeanErr => Snapshots == 0 ? 0 : ErrSum / Snapshots;
        public double MeanDrift => DriftSamples == 0 ? 0 : DriftSum / DriftSamples;
    }

    private sealed record Options
    {
        public int LatencyMs { get; init; } = 100;
        public int JitterMs { get; init; }
        public int ClientFps { get; init; } = 60;
        public int Millis { get; init; } = 40_000;
        /// <summary>跳过爬升期（单程延迟是限速逼近的，头几秒还在收敛）。</summary>
        public long WarmupMs { get; init; } = 10_000;
        public Func<int, int, bool> ServerWalkable { get; init; } = (_, _) => true;
        public Func<int, int, bool> ClientWalkable { get; init; } = (_, _) => true;
        /// <summary>跑到这个毫秒时服务端把玩家瞬移一段（模拟传送/击退/复活）。</summary>
        public int TeleportAtMs { get; init; } = -1;
        /// <summary>恒定意图（不换方向）：用来把"推进误差"与"意图时间线误差"分开。</summary>
        public bool ConstantIntent { get; init; }
        public float TeleportDx { get; init; } = 10f;
    }

    private sealed record Result(
        Collector Metrics, double ServerX, double ClientX, double ClientRenderedX,
        double EstimatedOneWayTicks, int SyntheticSpawns);

    /// <summary>跑一次完整链路。</summary>
    private static Result Run(Options options)
    {
        const double TickMs = 50.0;
        const double ServerSpeed = 10.0;

        var serverPredictor = new OwnMovePredictor(options.ServerWalkable);
        var clientPredictor = new OwnMovePredictor(options.ClientWalkable);
        serverPredictor.SetSpeed((float)ServerSpeed);
        clientPredictor.SetSpeed((float)ServerSpeed);

        var smoother = new ClientSmoother<OwnMoveState, MoveIntent>(clientPredictor);
        var collector = new Collector();

        var rng = new Random(20260917);
        long LatencyToWall() => options.LatencyMs + (options.JitterMs == 0 ? 0 : rng.Next(options.JitterMs + 1));

        // 服务端 tick T 发生在墙钟 T*TickMs（TickBase 只是让 tick 看起来像真实存档）
        const long TickBase = 1000;
        var serverState = OwnMoveState.FromContinuous(0f, 0f);
        ulong serverAppliedSeq = 0;
        var serverIntent = MoveIntent.Stop;

        var toClient = new List<(long At, long Tick, OwnMoveState State, ulong Seq)>();
        var toServer = new List<(long At, MoveIntent Intent, ulong Seq)>();
        var toServerSpawns = new List<(long At, float Dx)>();

        var frameMs = 1000.0 / options.ClientFps;
        var frames = (int)(options.Millis / frameMs);
        var nextServerTickWall = (long)TickMs;

        // 意图：每 37 tick 换一个方向（够久，才看得出转向时的表现）
        MoveIntent[] pattern = [new(1, 0), new(2, 0), new(0, 0), new(0, 1), new(1, 1), new(-1, 0)];
        var patternIndex = 0;
        var intentHoldTicks = options.ConstantIntent ? int.MaxValue : 37;
        var ticksSinceIntent = 0;

        ulong seq = 0;
        var currentIntent = pattern[0];
        var spawns = 0;

        for (var frame = 0; frame <= frames; frame++)
        {
            var frameWall = (long)Math.Round(frame * frameMs);

            // ── 服务端：把这个窗口内到点的 tick 都跑掉 ──
            while (nextServerTickWall <= frameWall)
            {
                var tick = TickBase + nextServerTickWall / (long)TickMs;
                var intent = serverIntent;
                serverPredictor.Step(ref serverState, intent, TickMs / 1000.0, 0);
                toClient.Add((nextServerTickWall + LatencyToWall(), tick, serverState, serverAppliedSeq));
                nextServerTickWall += (long)TickMs;
            }

            // ── 网络：投递到服务端（输入）与到客户端（快照）──
            for (var i = toServer.Count - 1; i >= 0; i--)
            {
                if (toServer[i].At > frameWall) continue;
                serverIntent = toServer[i].Intent;
                serverAppliedSeq = Math.Max(serverAppliedSeq, toServer[i].Seq);
                toServer.RemoveAt(i);
            }

            for (var i = toClient.Count - 1; i >= 0; i--)
            {
                if (toClient[i].At > frameWall) continue;
                var msg = toClient[i];
                smoother.OnSnapshot(
                    new NetSnapshot<OwnMoveState>(msg.Tick, msg.Seq, 1, msg.State, false),
                    frameWall);
                toClient.RemoveAt(i);
            }

            // ── 服务端侧事件：传送 ──
            if (options.TeleportAtMs >= 0 && toServerSpawns.Count == 0 && spawns == 0 &&
                frameWall >= options.TeleportAtMs)
            {
                serverState = OwnMoveState.FromContinuous(serverState.X + options.TeleportDx, serverState.Y);
                spawns++;
            }

            // ── 客户端：意图变化（走同一条延迟链路送到服务端）──
            if (ticksSinceIntent >= intentHoldTicks)
            {
                ticksSinceIntent = 0;
                patternIndex = (patternIndex + 1) % pattern.Length;
                currentIntent = pattern[patternIndex];
                seq++;
                smoother.SetIntent(seq, 1, currentIntent, frameWall);
                toServer.Add((frameWall + LatencyToWall(), currentIntent, seq));
                ticksSinceIntent = 0;
            }
            else
            {
                ticksSinceIntent++;
                // 首帧也要建立意图
                if (seq == 0)
                {
                    seq++;
                    smoother.SetIntent(seq, 1, currentIntent, frameWall);
                    toServer.Add((frameWall + LatencyToWall(), currentIntent, seq));
                }
            }

            // ── 客户端帧推进 ──
            smoother.AdvanceFrame(frameWall);

            if (frameWall >= options.WarmupMs) smoother.Metrics = collector;
        }

        return new Result(
            collector, serverState.X, smoother.State.X, smoother.RenderedState.X,
            smoother.EstimatedOneWayTicks, spawns);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(300)]
    public void ConstantIntentHasZeroSteadyStateError(int latencyMs)
    {
        // 只走一个方向、从不转向：没有任何意图时间线的不确定性，
        // 此时逐帧活推进与逐 tick 重放必须给出**同一个位置**，误差应当≈0。
        var r = Run(new Options { LatencyMs = latencyMs, ConstantIntent = true });
        Console.WriteLine(
            $"恒定意图 延迟 {latencyMs,3}ms | 接受 {r.Metrics.AcceptRate * 100,5:F1}% " +
            $"误差均值 {r.Metrics.MeanErr:F5} 峰值 {r.Metrics.ErrMax:F4} 漂移 {r.Metrics.MeanDrift:F4}");

        Assert.True(r.Metrics.MeanErr < 0.01, $"恒定意图下不应有稳态误差，实际 {r.Metrics.MeanErr:F4}");
        Assert.True(r.Metrics.AcceptRate > 0.9, $"接受率 {r.Metrics.AcceptRate:P1}");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 0)]
    [InlineData(100, 0)]
    [InlineData(200, 20)]
    [InlineData(300, 50)]
    public void MatchedClientKeepsPredictionAccurateAtAnyLatency(int latencyMs, int jitterMs)
    {
        var r = Run(new Options { LatencyMs = latencyMs, JitterMs = jitterMs });

        Console.WriteLine(
            $"延迟 {latencyMs,3}ms 抖动 {jitterMs,2}ms | 接受 {r.Metrics.AcceptRate * 100,5:F1}% " +
            $"平滑 {r.Metrics.BlendRate * 100,5:F2}% 直接贴 {r.Metrics.SnapRate * 100,5:F2}% " +
            $"重放 {r.Metrics.ReplaySnapshots,4}次 均 {r.Metrics.AvgReplay:F2} tick " +
            $"| 误差均值 {r.Metrics.MeanErr:F4} 峰值 {r.Metrics.ErrMax:F3} " +
            $"| 漂移 {r.Metrics.MeanDrift:F3} | 单程估计 {r.EstimatedOneWayTicks:F1} tick");

        // 重放确实在跑（机制被覆盖到）。
        Assert.True(r.Metrics.ReplaySnapshots > 0, "重放没有被触发，测试没覆盖到");

        // 客户端最终必须与服务端一致（预测 + 和解的意义所在）。
        Assert.True(Math.Abs(r.ServerX - r.ClientX) < 1.0,
            $"长时间跑下来客户端与服务端分叉：服务端 {r.ServerX:F2} vs 客户端 {r.ClientX:F2}");

        // 偏差不能饱和成"每帧硬贴"。
        Assert.True(r.Metrics.SnapRate < 0.2, $"回退率过高：{r.Metrics.SnapRate:P1}");
        Assert.True(r.Metrics.AcceptRate > 0.6, $"接受率过低：{r.Metrics.AcceptRate:P1}");

        // ⚠️ 已知残差（转向时）：输入"何时在服务端生效"只能估到 tick 边界，
        //    估计误差让重放把新方向提前/推迟最多约一个 tick；延迟越大、重放窗口越长，残差越大。
        //    实测 ≈ 0.15 × 单程 tick 数。彻底归零需要服务端带上"输入生效的那一 tick"。
        var bound = 0.25f * (1f + (float)r.EstimatedOneWayTicks);
        Assert.True(r.Metrics.MeanErr <= bound,
            $"稳态误差 {r.Metrics.MeanErr:F3} 超出转向残差上界 {bound:F3}（延迟 {latencyMs}ms）");
    }

    [Fact]
    public void ServerOnlyWallForcesContinuousCorrectionAndStillFollowsTheServer()
    {
        // 客户端不知道有墙，服务端知道 → 每份快照都要把客户端拉回来。
        // 这是"两端世界真相不一致"的典型：接受率会掉，但客户端最终必须跟着服务端停在墙前。
        var opts = new Options
        {
            LatencyMs = 100,
            ServerWalkable = (x, _) => x < 5,
            ClientWalkable = (_, _) => true,
        };
        var r = Run(opts);
        // 同一延迟下的"两端同构"基线，用来做**相对**比较（绝对阈值对参数太敏感）。
        var matched = Run(new Options { LatencyMs = 100 });

        Console.WriteLine(
            $"服务端有墙 | 接受 {r.Metrics.AcceptRate * 100:F1}%（基线 {matched.Metrics.AcceptRate * 100:F1}%）" +
            $" 平滑 {r.Metrics.BlendRate * 100:F1}% 直接贴 {r.Metrics.SnapRate * 100:F2}% " +
            $"| 误差均值 {r.Metrics.MeanErr:F3} " +
            $"| 服务端 X={r.ServerX:F2} 客户端 X={r.ClientX:F2} 渲染 X={r.ClientRenderedX:F2}");

        Assert.True(r.Metrics.AcceptRate < matched.Metrics.AcceptRate - 0.1,
            "世界真相不一致时接受率应该明显下降");
        Assert.True(r.Metrics.MeanErr > matched.Metrics.MeanErr, "误差应该明显更大");
        Assert.True(r.Metrics.BlendRate > 0.2, "应该靠平滑把客户端拉回来，而不是每帧硬贴");
        // 关键：校正真的起作用 —— 客户端没有越过服务端的墙
        Assert.True(r.ClientX < 6.0, $"客户端没被拉回墙前：X={r.ClientX:F2}");
        // 渲染层是"平滑后的表现"：允许在 CorrectionSnapAbove（1.5 格）附近过渡（这里稳态约 1.3 格），
        // 但**不能**离模拟位置越来越远 —— 那就说明平滑退化成"持续拖影"了。
        Assert.True(r.ClientRenderedX < r.ClientX + 1.55,
            $"渲染离模拟太远（平滑变成拖影）：渲染 X={r.ClientRenderedX:F2} 模拟 X={r.ClientX:F2}");
    }

    [Fact]
    public void TeleportIsReportedAsLargeRollback()
    {
        var opts = new Options { LatencyMs = 100, TeleportAtMs = 20_000, TeleportDx = 12f };
        var r = Run(opts);

        Console.WriteLine(
            $"传送 | 接受 {r.Metrics.AcceptRate * 100:F1}% 平滑 {r.Metrics.BlendRate * 100:F1}% " +
            $"直接贴 {r.Metrics.SnapRate * 100:F2}%（{r.Metrics.Snapped} 次）| 误差峰值 {r.Metrics.ErrMax:F2}");

        Assert.Equal(1, r.SyntheticSpawns);
        Assert.True(r.Metrics.Snapped >= 1, "传送必须被识别为【直接贴】（大规模回退）");
        Assert.True(r.Metrics.ErrMax > 1.5f);
    }

    [Fact]
    public void ConstantIntentIsExactWhichIsolatesTheRemainingErrorToIntentTiming()
    {
        // 这条是"自证"：完全不加延迟、也不转向时，逐帧活推进与逐 tick 重放必须**逐位一致**。
        // 既然它是 0 误差，那么转向场景里剩下的误差就只能来自"输入何时在服务端生效"的估计。
        //
        // ⇒ 待办（服务端）：快照里除了 last_applied_seq，再带上**它生效的那一 tick**，
        //    这样 effectiveTick 就是精确值，转向误差可以直接归零。
        var r = Run(new Options { LatencyMs = 0, ConstantIntent = true });
        Assert.Equal(0.0, r.Metrics.MeanErr);
        Assert.Equal(0.0, r.Metrics.ErrMax);
        Assert.Equal(0.0, r.Metrics.MeanDrift);
    }

    [Fact]
    public void ReplayWindowEqualsTheInFlightWindow()
    {
        // 重放窗口 = "已经在路上、服务端还没处理到"的那段输入 ≈ 单程延迟（+ 一点量化）。
        // 这正是经典 Quake 式和解的样子：**把 RTT 那一段输入重走一遍**。
        // （早先我以为重放长度与延迟无关 —— 那是"不加领先量"的错误设计下的结论。）
        var fast = Run(new Options { LatencyMs = 50 });
        var slow = Run(new Options { LatencyMs = 300 });

        Console.WriteLine(
            $"重放窗口：50ms→{fast.Metrics.AvgReplay:F2} tick（单程估计 {fast.EstimatedOneWayTicks:F1}）" +
            $"；300ms→{slow.Metrics.AvgReplay:F2} tick（单程估计 {slow.EstimatedOneWayTicks:F1}）");

        // 重放窗口 ≈ 在途窗口。注意单程延迟是限速逼近的，所以用它做精确等式会抖；
        // 这里断言"随延迟显著增长"与"不超上限"。
        Assert.True(fast.Metrics.AvgReplay >= 1.0, "低延迟下也应有重放");
        Assert.True(slow.Metrics.AvgReplay > fast.Metrics.AvgReplay + 3.0,
            "重放窗口应随单程延迟增长");
        // 上限保护：不能无限重放
        Assert.Equal(0, slow.Metrics.Clamped);
    }
}
