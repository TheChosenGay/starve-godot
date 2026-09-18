using Starve.Netcode;

namespace Starve.Netcode.Tests;

/// <summary>
/// 【序号锚定和解】大流量压测：**多玩家 + 长时 + 频繁换向/停下 + 各自的延迟/抖动/丢包/乱序**。
///
/// 单场景的多种子用例（<see cref="SequenceAnchoredLinkTests"/>）保证"每种条件都测到"，
/// 这里保证"**规模与时长够**"：正式环境里是几十个玩家同时跑几分钟，所以必须有量级接近的用例，
/// 否则只会在上线后才暴露规模相关的问题（队列积压、序号缺口、追步、内存）。
///
/// 每个玩家一条独立队列/独立链路，断言与单玩家一致：
/// 冗余窗口兜得住 ⇒ 同序号状态严格相等、零假失配；兜不住 ⇒ 有界且末尾自愈。
/// </summary>
public sealed class SequenceAnchoredSoakTests
{
    private const long Tick0 = 1000;
    private const ulong Epoch = 7;
    private const double TickMs = 50.0;
    private const float Speed = 10f;
    private const int FrameFps = 60;
    private const int StepBudget = 3;
    /// <summary>上行冗余条数（与服务端消费预算无关；见 NetcodeConfig.RedundantOps）。</summary>
    private const int RedundantOps = 4;

    private static long WallOf(double tick) => (long)Math.Round(tick * TickMs);
    private static NetSnapshot<FakeState> Snap(long tick, float x, ulong appliedSeq) =>
        new(tick, appliedSeq, Epoch, new FakeState { X = x }, false);

    /// <summary>服务端模拟器：一条操作队列 + 按序号连续消费 + 追步（K 条配 K 步）。</summary>
    private sealed class ServerLink
    {
        private readonly List<(ulong Seq, int Dx)> _pending = new();
        private readonly FakeModel _model = new() { Speed = Speed };
        private ulong _consumed;
        private int _gapWait;

        public FakeState State = new();
        public ulong Applied;
        public long Tick = Tick0;
        public int MaxBacklog, ExtraSteps, Received, Deduped, Desyncs;

        public void Receive(ulong seq, int dx)
        {
            Received++;
            if (seq <= _consumed) { Deduped++; return; }
            foreach (var p in _pending)
            {
                if (p.Seq != seq) continue;
                Deduped++;
                return;
            }

            var at = _pending.Count;
            while (at > 0 && _pending[at - 1].Seq > seq) at--;
            _pending.Insert(at, (seq, dx));
        }

        public void AdvanceTo(long tick)
        {
            while (Tick < tick)
            {
                Tick++;
                MaxBacklog = Math.Max(MaxBacklog, _pending.Count);
                var steps = 0;
                while (_pending.Count > 0 && steps < StepBudget)
                {
                    if (_consumed == 0) _consumed = _pending[0].Seq - 1;
                    if (_pending[0].Seq != _consumed + 1)
                    {
                        if (++_gapWait < 3) break;   // 与服务端 maxGapWaitTicks 一致
                        Desyncs++;
                    }
                    _gapWait = 0;

                    var (seq, dx) = _pending[0];
                    _pending.RemoveAt(0);
                    _consumed = seq;
                    _model.Step(ref State, new FakeAction { Dx = dx }, TickMs / 1000.0, 0);
                    Applied = seq;
                    steps++;
                }
                if (steps > 1) ExtraSteps += steps - 1;
            }
        }
    }

    private sealed class Player
    {
        public required ClientSmoother<FakeState, FakeAction> Client;
        public required ServerLink Server;
        public required Random Rng;
        public required int LatencyMs;
        public required int JitterMs;
        public required int LossPercent;
        public required List<(long At, ulong Seq, int Dx)> Up;
        public required List<(long At, long Tick, float X, ulong Seq)> Down;
        public required List<ClientSmoother<FakeState, FakeAction>.OpRef> Batch;
        public long LastSentSeq;
        public int Direction = 1;
        public int Snapshots, Mismatch, Blended, Snapped, DroppedUp, DroppedDown;
        public double MaxErr;
        public bool TailOk = true;
        public int InputLatency = -1;
        private readonly Queue<bool> _recent = new();

        public void Note(bool matched)
        {
            if (!matched) Mismatch++;
            _recent.Enqueue(matched);
            while (_recent.Count > 20) _recent.Dequeue();
            if (_recent.Count == 20 && _recent.Any(x => !x)) TailOk = false;
            else if (_recent.Count == 20) TailOk = true;
        }
    }

    private sealed class World
    {
        private readonly List<Player> _players = new();
        private readonly int _ticks;
        private readonly int _seed;

        public World(int playerCount, int ticks, int seed)
        {
            _ticks = ticks;
            _seed = seed;
            for (var i = 0; i < playerCount; i++)
            {
                var rng = new Random(seed + i * 104729);
                // 混合链路：有的好、有的差（模拟正式环境里的不同网络）
                var latency = 30 + rng.Next(0, 4) * 70;
                var jitter = rng.Next(0, 3) * 20;
                var loss = rng.Next(0, 5) * 6;
                var client = new ClientSmoother<FakeState, FakeAction>(
                    new FakeModel { Speed = Speed },
                    new NetcodeConfig { ClockMaxStepMs = 0, TickIndexedIntents = true });
                client.OnSnapshot(Snap(Tick0, 0f, 0), WallOf(Tick0));
                _players.Add(new Player
                {
                    Client = client,
                    Server = new ServerLink(),
                    Rng = rng,
                    LatencyMs = latency,
                    JitterMs = jitter,
                    LossPercent = loss,
                    Up = new List<(long, ulong, int)>(),
                    Down = new List<(long, long, float, ulong)>(),
                    Batch = new List<ClientSmoother<FakeState, FakeAction>.OpRef>(),
                });
            }
        }

        public IReadOnlyList<Player> Players => _players;

        private long Jitter(Player p) => p.JitterMs == 0 ? 0 : p.Rng.Next(-p.JitterMs, p.JitterMs + 1);

        public void Run()
        {
            var t0 = WallOf(Tick0);
            var frames = _ticks * FrameFps / 20;
            long lastFrame = t0;

            for (var f = 1; f <= frames; f++)
            {
                var nowMs = t0 + (long)Math.Round(f * 1000.0 / FrameFps);
                lastFrame = nowMs;
                var tick = Tick0 + (long)(f * 1000.0 / FrameFps / TickMs);

                foreach (var p in _players)
                {
                    // 频繁换向/停下（每 ~0.4 秒一次）：制造大量操作边界
                    if (f % 24 == 0)
                    {
                        var roll = p.Rng.Next(10);
                        p.Direction = roll switch { < 4 => 1, < 8 => -1, _ => 0 };
                        p.Client.SetIntent(1, Epoch, new FakeAction { Dx = p.Direction },
                            nowMs + p.LatencyMs / 2);   // 玩家在"自己那侧"按下（相位不同）
                    }

                    p.Server.AdvanceTo(tick);

                    // 上行：整批未确认窗口（冗余）；整包可能丢
                    p.Client.CollectUnackedOps(p.Batch, max: RedundantOps);
                    var fresh = false;
                    foreach (var op in p.Batch)
                    {
                        if (op.Seq > (ulong)p.LastSentSeq) { fresh = true; break; }
                    }
                    if (fresh && !(p.LossPercent > 0 && p.Rng.Next(100) < p.LossPercent))
                    {
                        var at = nowMs + p.LatencyMs + Jitter(p);
                        foreach (var op in p.Batch)
                        {
                            if (op.Seq <= (ulong)p.LastSentSeq) continue;
                            p.Up.Add((at, op.Seq, op.Action.Dx));
                            p.LastSentSeq = (long)op.Seq;
                        }
                    }

                    for (var i = p.Up.Count - 1; i >= 0; i--)
                    {
                        if (p.Up[i].At > nowMs) continue;
                        var op = p.Up[i];
                        p.Up.RemoveAt(i);
                        p.Server.Receive(op.Seq, op.Dx);
                    }

                    if (f % (FrameFps / 20) == 0)
                    {
                        if (p.LossPercent > 0 && p.Rng.Next(100) < p.LossPercent) p.DroppedDown++;
                        else p.Down.Add((nowMs + p.LatencyMs + Jitter(p), p.Server.Tick, p.Server.State.X, p.Server.Applied));
                    }

                    p.Client.AdvanceFrame(nowMs);

                    for (var i = p.Down.Count - 1; i >= 0; i--)
                    {
                        if (p.Down[i].At > nowMs) continue;
                        var s = p.Down[i];
                        p.Down.RemoveAt(i);
                        if (s.Seq == 0) continue;

                        var report = p.Client.OnSnapshot(Snap(s.Tick, s.X, s.Seq), nowMs);
                        if (report.Kind == CorrectionKind.Stale) continue;
                        p.Snapshots++;
                        p.MaxErr = Math.Max(p.MaxErr, report.Err);
                        if (report.Kind == CorrectionKind.Blended) p.Blended++;
                        if (report.Kind == CorrectionKind.Snapped) p.Snapped++;
                        p.Note(p.Client.TryGetOpState(s.Seq, out var mine) &&
                               MathF.Abs(mine.X - s.X) <= 1e-4f);
                    }
                }
            }

            foreach (var p in _players) p.InputLatency = MeasureLatency(p, lastFrame);
        }

        private static int MeasureLatency(Player p, long atMs)
        {
            // ⚠️ 必须从"当前帧"接着推进：跳时间会让链条一次性跨很多 tick，
            //    测出来的就变成"跳了多少"而不是输入延迟（这个坑踩过一次）。
            var startTick = p.Client.StateTick;
            var x0 = p.Client.State.X;
            var dir = p.Direction == -1 ? 1 : -1;
            p.Client.SetIntent(1, Epoch, new FakeAction { Dx = dir }, atMs);
            for (var i = 1; i <= 6; i++)
            {
                p.Client.AdvanceFrame(atMs + i * 17);
                if (MathF.Abs(p.Client.State.X - x0) > 1e-4f) return (int)(p.Client.StateTick - startTick);
            }

            return int.MaxValue;
        }
    }

    [Theory]
    [InlineData(8, 600, 11)]     // 8 人 × 30 秒
    [InlineData(16, 900, 12)]    // 16 人 × 45 秒（~14k 条操作）
    [InlineData(24, 600, 13)]    // 24 人 × 30 秒
    public void ManyPlayersSoakStaysCoherent(int playerCount, int ticks, int seed)
    {
        var world = new World(playerCount, ticks, seed);
        world.Run();

        var ops = world.Players.Sum(p => p.Server.Received);
        var dedup = world.Players.Sum(p => p.Server.Deduped);
        var snaps = world.Players.Sum(p => p.Snapshots);
        var mismatch = world.Players.Sum(p => p.Mismatch);
        var blended = world.Players.Sum(p => p.Blended);
        var snapped = world.Players.Sum(p => p.Snapped);
        var desyncs = world.Players.Sum(p => p.Server.Desyncs);
        var backlog = world.Players.Max(p => p.Server.MaxBacklog);
        var extra = world.Players.Sum(p => p.Server.ExtraSteps);
        var maxErr = world.Players.Max(p => p.MaxErr);
        var latency = world.Players.Max(p => p.InputLatency);

        Console.WriteLine(
            $"压测 {playerCount} 人 × {ticks / 20}s | 操作 {ops}（去重 {dedup}）快照 {snaps} | " +
            $"失配 {mismatch} 跳过缺口 {desyncs} 平滑 {blended} 直接贴 {snapped} 误差峰值 {maxErr:F3} | " +
            $"队列峰值 {backlog} 追步额外 {extra} | 输入延迟峰值 {latency} tick");

        Assert.True(ops > 1000, "测试自身有问题：操作量太小");
        Assert.True(latency <= 1, $"输入延迟 {latency} tick > 1");
        Assert.True(backlog <= StepBudget + 2, $"队列积压 {backlog} 超出追步能力");
        Assert.True(maxErr <= 1.5f, $"误差峰值 {maxErr:F3} 超过直接贴阈值（错乱）");
        Assert.All(world.Players, p => Assert.True(float.IsFinite(p.Client.State.X), "不得出现 NaN"));

        // 末态自愈：每个玩家的最后 20 份快照都必须重新完全一致
        Assert.All(world.Players, p => Assert.True(p.TailOk, "末尾没有回到一致（没有自愈）"));
        // 失配必须能被"跳过缺口"解释（没有悄悄发散）
        Assert.True(mismatch <= desyncs * 3 + world.Players.Count * 5,
            $"失配 {mismatch} 与跳过缺口 {desyncs} 不成比例");
    }

    /// <summary>
    /// 断网 2 秒再恢复：输入会丢一大段（远超冗余窗口）⇒ 服务端跳缺口、客户端校正。
    /// 要求：有界、不崩、**恢复后重新完全一致**（末尾自愈），且输入延迟仍然 ≤1 tick。
    /// </summary>
    [Fact]
    public void BlackoutRecoversAndResettles()
    {
        var world = new World(playerCount: 4, ticks: 400, seed: 21);
        var blackout = false;

        // 直接复用 World.Run 的结构太绕：这里用"超长延迟"模拟断网段（包全部压在后面）
        var t0 = WallOf(Tick0);
        var frames = 400 * FrameFps / 20;
        foreach (var p in world.Players) p.LatencyMs = 100;

        for (var f = 1; f <= frames; f++)
        {
            var nowMs = t0 + (long)Math.Round(f * 1000.0 / FrameFps);
            var tick = Tick0 + (long)(f * 1000.0 / FrameFps / TickMs);
            blackout = f > 60 && f <= 60 + 2 * FrameFps;   // 第 1~3 秒断网

            foreach (var p in world.Players)
            {
                if (f % 24 == 0)
                {
                    p.Direction = p.Rng.Next(3) - 1;
                    p.Client.SetIntent(1, Epoch, new FakeAction { Dx = p.Direction }, nowMs);
                }

                p.Server.AdvanceTo(tick);
                p.Client.CollectUnackedOps(p.Batch, max: RedundantOps);
                var fresh = false;
                foreach (var op in p.Batch)
                {
                    if (op.Seq > (ulong)p.LastSentSeq) { fresh = true; break; }
                }

                if (fresh && !blackout)
                {
                    var at = nowMs + p.LatencyMs;
                    foreach (var op in p.Batch)
                    {
                        if (op.Seq <= (ulong)p.LastSentSeq) continue;
                        p.Up.Add((at, op.Seq, op.Action.Dx));
                        p.LastSentSeq = (long)op.Seq;
                    }
                }

                for (var i = p.Up.Count - 1; i >= 0; i--)
                {
                    if (p.Up[i].At > nowMs) continue;
                    var op = p.Up[i];
                    p.Up.RemoveAt(i);
                    p.Server.Receive(op.Seq, op.Dx);
                }

                if (!blackout && f % (FrameFps / 20) == 0)
                    p.Down.Add((nowMs + p.LatencyMs, p.Server.Tick, p.Server.State.X, p.Server.Applied));

                p.Client.AdvanceFrame(nowMs);

                for (var i = p.Down.Count - 1; i >= 0; i--)
                {
                    if (p.Down[i].At > nowMs) continue;
                    var s = p.Down[i];
                    p.Down.RemoveAt(i);
                    if (s.Seq == 0) continue;
                    var report = p.Client.OnSnapshot(Snap(s.Tick, s.X, s.Seq), nowMs);
                    if (report.Kind == CorrectionKind.Stale) continue;
                    p.Snapshots++;
                    p.MaxErr = Math.Max(p.MaxErr, report.Err);
                    if (report.Kind == CorrectionKind.Blended) p.Blended++;
                    if (report.Kind == CorrectionKind.Snapped) p.Snapped++;
                    p.Note(p.Client.TryGetOpState(s.Seq, out var mine) &&
                           MathF.Abs(mine.X - s.X) <= 1e-4f);
                }
            }
        }

        var desyncs = world.Players.Sum(p => p.Server.Desyncs);
        Console.WriteLine(
            $"断网 2s 恢复 | 跳过缺口 {desyncs} 平滑 {world.Players.Sum(p => p.Blended)} " +
            $"直接贴 {world.Players.Sum(p => p.Snapped)} 误差峰值 {world.Players.Max(p => p.MaxErr):F3} | " +
            $"末尾自愈 {(world.Players.All(p => p.TailOk) ? "是" : "否")}");

        Assert.True(desyncs >= 1, "断网丢一大段输入，服务端应当至少跳过一次缺口");
        // 断网期间两边的输入流**真的不同**（那几秒的操作永远收不到）⇒ 只能硬校正。
        // 要求：误差不超过断网期间积累的位移（有界）、不变成校正风暴、恢复后重新完全一致。
        Assert.True(world.Players.Max(p => p.MaxErr) <= 12f,
            $"断网恢复的误差 {world.Players.Max(p => p.MaxErr):F2} 超出断网期间可能积累的量（在发散？）");
        Assert.True(world.Players.Sum(p => p.Snapped) <= 60,
            $"断网后出现校正风暴：{world.Players.Sum(p => p.Snapped)} 次直接贴");
        Assert.All(world.Players, p => Assert.True(p.TailOk, "断网恢复后没有重新一致"));
        Assert.All(world.Players, p => Assert.True(float.IsFinite(p.Client.State.X)));
    }
}
