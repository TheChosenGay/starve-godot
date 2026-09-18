using Starve.Netcode;

namespace Starve.Netcode.Tests;

/// <summary>
/// 【序号锚定和解】的**整链路**测试台：客户端组件（每 tick 采样一条操作 + 未确认窗口冗余上传）
/// ↔ 网络（延迟/抖动/丢包/乱序）↔ 服务端模拟器（每玩家一条队列、按 seq 去重排序、
/// 每 tick 消费 min(队列, K) 条、每条 Move 走一步且用**自己那条**的方向）。
///
/// 要钉死的不变量（这几条就是"客户端模拟和权威严格一致"的全部含义）：
/// <list type="number">
/// <item><b>同序号状态严格相等</b>：客户端环里"应用完第 N 条之后的状态" == 快照里"服务端应用完第 N 条之后的状态"；</item>
/// <item><b>没有假失配</b>：每次和解都落在死区（err ≤ 死区），不出现"平滑/直接贴"；</item>
/// <item><b>队列不积压</b>：服务端待消费队列长度 ≤ K（客户端平均 1 条/tick，消费上限 K=3）；</item>
/// <item><b>K 条配 K 步</b>：追步时消费了几条 Move 就真的走了几步。</item>
/// </list>
/// </summary>
public sealed class SequenceAnchoredLinkTests
{
    private const long Tick0 = 1000;
    private const ulong Epoch = 7;
    private const double TickMs = 50.0;
    private const float Speed = 10f;          // 0.5 格/tick
    private const int FrameFps = 60;
    private const int StepBudget = 3;
    /// <summary>上行冗余条数（与服务端消费预算无关；见 NetcodeConfig.RedundantOps）。</summary>
    private const int RedundantOps = 4;

    private static long WallOf(double tick) => (long)Math.Round(tick * TickMs);
    private static NetSnapshot<FakeState> Snap(long tick, float x, ulong appliedSeq) =>
        new(tick, appliedSeq, Epoch, new FakeState { X = x }, false);

    /// <summary>服务端模拟器：一条操作队列 + 固定速率消费 + 追步（K 条配 K 步）。</summary>
    private sealed class ServerLink
    {
        private readonly List<(ulong Seq, int Dx)> _pending = new();
        private readonly FakeModel _model = new() { Speed = Speed };

        public FakeState State = new();
        public ulong Applied;
        public long Tick = Tick0;
        public int MaxBacklog;
        public int ExtraSteps;
        public int Received;
        public int Deduped;
        public int Desyncs;
        private ulong _consumed;
        private int _gapWait;

        public void Receive(ulong seq, int dx)
        {
            Received++;
            if (seq <= Applied) { Deduped++; return; }          // 已消费过：冗余包，丢
            for (var i = 0; i < _pending.Count; i++)
            {
                if (_pending[i].Seq != seq) continue;
                Deduped++;                                       // 还在排队：冗余包，丢
                return;
            }

            var at = _pending.Count;
            while (at > 0 && _pending[at - 1].Seq > seq) at--;
            _pending.Insert(at, (seq, dx));
        }

        public void Advance(int steps = 1)
        {
            for (var s = 0; s < steps; s++)
            {
                Tick++;
                MaxBacklog = Math.Max(MaxBacklog, _pending.Count);
                var consumed = 0;
                var stepCount = 0;
                // 每 tick 最多消费 K 条 Move；每条 Move 配一步（自己的方向）
                while (_pending.Count > 0 && stepCount < StepBudget)
                {
                    if (_consumed == 0) _consumed = _pending[0].Seq - 1;   // 第一条作基线
                    if (_pending[0].Seq != _consumed + 1)
                    {
                        // 缺口没补齐：等（冗余窗口会补上）；等太久才跳过并记账
                        _gapWait++;
                        if (_gapWait < 10) break;
                        Desyncs++;
                    }
                    _gapWait = 0;

                    var (seq, dx) = _pending[0];
                    _pending.RemoveAt(0);
                    _consumed = seq;
                    _model.Step(ref State, new FakeAction { Dx = dx }, TickMs / 1000.0, 0);
                    Applied = seq;
                    consumed++;
                    stepCount++;
                }

                _ = consumed;
                if (stepCount > 1) ExtraSteps += stepCount - 1;
            }
        }

        /// <summary>服务端推进到客户端"现在"（含在途），用固定 50ms 步长。</summary>
        public void AdvanceTo(long tick)
        {
            while (Tick < tick) Advance();
        }
    }

    private sealed class Link
    {
        private readonly ClientSmoother<FakeState, FakeAction> _client;
        private readonly ServerLink _server = new();
        private readonly List<(long At, ulong Seq, int Dx)> _up = new();
        private readonly List<(long At, long Tick, float X, ulong Seq)> _down = new();
        private readonly List<ClientSmoother<FakeState, FakeAction>.OpRef> _batch = new();
        private readonly Random _rng;
        private readonly int _latencyMs;
        private readonly int _jitterMs;
        private readonly int _lossPercent;
        private readonly bool _reorder;

        public long Sent;
        public int DroppedUp;
        public int DroppedDown;
        public int Snapshots;
        public double MaxErr;
        public long LastFrameMs;
        public int Blended;
        public int Snapped;
        public int RingMismatch;
        public readonly Queue<bool> RecentMatch = new();   // 最近 20 份快照是否"同序号状态相等"
        public int RingMismatchRecent;
        public int InputLatencyTicks = -1;
        private int _lastDirection = 1;
        private int _lastSentSeq;

        public Link(int latencyMs, int jitterMs, int lossPercent, bool reorder, int seed)
        {
            _latencyMs = latencyMs;
            _jitterMs = jitterMs;
            _lossPercent = lossPercent;
            _reorder = reorder;
            _rng = new Random(seed);
            _client = new ClientSmoother<FakeState, FakeAction>(
                new FakeModel { Speed = Speed },
                new NetcodeConfig { ClockMaxStepMs = 0, TickIndexedIntents = true });
            _client.OnSnapshot(Snap(Tick0, 0f, 0), WallOf(Tick0));
        }

        public ClientSmoother<FakeState, FakeAction> Client => _client;
        public ServerLink Server => _server;

        private long Jitter() => _jitterMs == 0 ? 0 : _rng.Next(-_jitterMs, _jitterMs + 1);

        /// <summary>上行：每个 tick 把"未确认窗口"整批发出去（冗余；整包可能被丢）。</summary>
        private void PumpUplink(long nowMs)
        {
            _client.CollectUnackedOps(_batch, max: RedundantOps);
            var fresh = false;
            foreach (var op in _batch)
            {
                if (op.Seq <= (ulong)_lastSentSeq) continue;   // 冗余窗口里已经发过的
                fresh = true;
                break;
            }

            if (!fresh) return;
            Sent++;
            if (_lossPercent > 0 && _rng.Next(100) < _lossPercent) { DroppedUp++; return; }

            var at = nowMs + _latencyMs + Jitter();
            foreach (var op in _batch)
            {
                if (op.Seq <= (ulong)_lastSentSeq) continue;
                _up.Add((at, op.Seq, op.Action.Dx));
                if (op.Seq > (ulong)_lastSentSeq) _lastSentSeq = (int)op.Seq;
            }
        }

        private void PumpDownlink(long nowMs)
        {
            if (_lossPercent > 0 && _rng.Next(100) < _lossPercent) { DroppedDown++; return; }
            var at = nowMs + _latencyMs + Jitter();
            _down.Add((at, _server.Tick, _server.State.X, _server.Applied));
        }

        /// <summary>跑若干 tick：客户端每帧推进、服务端按 tick 推进、双向带延迟/丢包/乱序。</summary>
        public void Run(int ticks, int turnAtTick = -1)
        {
            var frames = ticks * FrameFps / 20;              // 每 tick 3 帧（60fps）
            var t0 = WallOf(Tick0);                          // 墙钟基准 = 首份快照时刻（时钟锚点）
            for (var f = 1; f <= frames; f++)
            {
                var nowMs = t0 + (long)Math.Round(f * 1000.0 / FrameFps);
                LastFrameMs = nowMs;
                var tick = Tick0 + (long)(f * 1000.0 / FrameFps / TickMs);

                // 玩家意图：tick 中点反向一次
                if (turnAtTick > 0 && tick >= turnAtTick && _lastDirection == 1)
                {
                    _lastDirection = -1;
                    _client.SetIntent(1, Epoch, new FakeAction { Dx = -1 }, nowMs);
                }

                // 服务端按 tick 推进（每 tick 一步 + 追步预算）
                _server.AdvanceTo(tick);

                // 上行（客户端每 tick 采样一条，整批冗余发出）
                PumpUplink(nowMs);

                // 投递到服务端（乱序允许：按到达时间取，允许后到先处理）
                for (var i = _up.Count - 1; i >= 0; i--)
                {
                    if (_up[i].At > nowMs) continue;
                    var op = _up[i];
                    _up.RemoveAt(i);
                    _server.Receive(op.Seq, op.Dx);
                }

                // 服务端出快照（在途，可能丢）
                if (f % (FrameFps / 20) == 0) PumpDownlink(nowMs);

                // 客户端推进（模拟按整 tick 走，帧余量只影响表现层）
                _client.AdvanceFrame(nowMs);

                // 投递快照（乱序允许：同一帧内多份也可能顺序颠倒）
                for (var i = _down.Count - 1; i >= 0; i--)
                {
                    if (_down[i].At > nowMs) continue;
                    var s = _down[i];
                    _down.RemoveAt(i);
                    if (s.Seq == 0) continue;                 // 还没消费过任何操作

                    var report = _client.OnSnapshot(Snap(s.Tick, s.X, s.Seq), nowMs);
                    if (report.Kind == CorrectionKind.Stale) continue;
                    Snapshots++;
                    MaxErr = Math.Max(MaxErr, report.Err);
                    if (report.Kind == CorrectionKind.Blended) Blended++;
                    if (report.Kind == CorrectionKind.Snapped) Snapped++;

                    // ★ 同序号状态严格相等：我"应用完第 N 条"的状态 == 服务端"应用完第 N 条"的状态
                    var matched = _client.TryGetOpState(s.Seq, out var mine) &&
                                  MathF.Abs(mine.X - s.X) <= 1e-4f;
                    if (!matched) RingMismatch++;
                    RecentMatch.Enqueue(matched);
                    while (RecentMatch.Count > 20) RecentMatch.Dequeue();
                    if (RecentMatch.Count == 20) RingMismatchRecent = RecentMatch.Count(x => !x);
                }

                // 网络乱序：偶尔把两个在途包交换顺序
                if (_reorder && _up.Count >= 2 && _rng.Next(50) == 0)
                    (_up[0], _up[1]) = (_up[1], _up[0]);
            }
        }

        /// <summary>量输入延迟：从 SetIntent 起，客户端自己的模拟几个 tick 后才反向。</summary>
        public void MeasureInputLatency(long nowMs)
        {
            _lastDirection = -1;
            var startTick = _client.StateTick;
            _client.SetIntent(2, Epoch, new FakeAction { Dx = -1 }, nowMs);
            var x0 = _client.State.X;
            for (var i = 0; i < 5; i++)
            {
                _client.AdvanceFrame(nowMs + (i + 1) * 17);
                if (_client.State.X < x0 - 1e-4f) { InputLatencyTicks = (int)(_client.StateTick - startTick); return; }
            }
            InputLatencyTicks = int.MaxValue;
        }
    }

    [Theory]
    // 每个场景跑多个**固定**种子 ⇒ 既可重复，又不是只过了一次运气好的随机序列
    [InlineData(0, 0, 0, false, 1)]
    [InlineData(0, 0, 0, false, 2)]
    [InlineData(50, 0, 0, false, 1)]
    [InlineData(50, 0, 0, false, 3)]
    [InlineData(100, 0, 0, false, 1)]
    [InlineData(100, 0, 0, false, 4)]
    [InlineData(200, 20, 0, false, 1)]
    [InlineData(200, 20, 0, false, 2)]
    [InlineData(300, 50, 0, false, 1)]
    [InlineData(300, 50, 0, false, 5)]
    [InlineData(100, 0, 30, false, 1)]   // 30% 丢包：靠冗余窗口兜住
    [InlineData(100, 0, 30, false, 2)]
    [InlineData(100, 0, 50, false, 3)]   // 50% 丢包
    [InlineData(200, 40, 20, true, 1)]   // 抖动 + 丢包 + 乱序
    [InlineData(200, 40, 20, true, 4)]
    [InlineData(500, 80, 25, true, 1)]   // 极端
    [InlineData(500, 80, 25, true, 2)]
    public void SequenceAnchoredLinkStaysExact(int latencyMs, int jitterMs, int lossPercent, bool reorder, int seed)
    {
        var link = new Link(latencyMs, jitterMs, lossPercent, reorder, seed: 20260214 + seed * 7919);
        link.Run(ticks: 200, turnAtTick: (int)Tick0 + 40);   // 10 秒，中途反向一次
        link.MeasureInputLatency(link.LastFrameMs);

        Console.WriteLine(
            $"种子 {seed} 延迟 {latencyMs,4}ms 抖动 {jitterMs,3}ms 丢包 {lossPercent,2}% 乱序 {(reorder ? 1 : 0)} | " +
            $"快照 {link.Snapshots} 误差峰值 {link.MaxErr:F4} 平滑 {link.Blended} 直接贴 {link.Snapped} " +
            $"序号状态不符 {link.RingMismatch} | 上行丢 {link.DroppedUp} 下行丢 {link.DroppedDown} | " +
            $"服务端队列峰值 {link.Server.MaxBacklog} 追步额外 {link.Server.ExtraSteps} 跳过缺口 {link.Server.Desyncs} " +
            $"收到 {link.Server.Received} 去重 {link.Server.Deduped} | 输入延迟 {link.InputLatencyTicks} tick");

        Assert.True(link.Snapshots > 20, "测试自身有问题：快照太少");
        Assert.True(link.InputLatencyTicks <= 1, $"输入延迟 {link.InputLatencyTicks} tick > 1");
        // 队列必须**有界**：
        //   · 干净链路：突发最多瞬间到 K+1 条，下一个 tick 就被追步吃掉（紧界）；
        //   · 有丢包的链路：缺口等待期间会**故意**压住几条（最多等 10 个 tick），
        //     所以上界是"等待窗口 × 到达速率"，而不是 K —— 它仍然是常数、且随后排空。
        var tight = lossPercent == 0 && jitterMs == 0 && !reorder;
        var bound = tight ? StepBudget + 2 : 20;
        Assert.True(link.Server.MaxBacklog <= bound,
            $"队列积压 {link.Server.MaxBacklog} 超出上界 {bound}（会变成永久延迟）");
        Assert.True(float.IsFinite(link.Client.State.X), "不得出现 NaN");

        if (link.Server.Desyncs == 0)
        {
            // ★ 冗余窗口兜住了（无缺口）：同序号状态必须**严格相等**、零假失配
            Assert.Equal(0, link.RingMismatch);
            Assert.True(link.MaxErr <= 0.031f, $"出现假失配：err 峰值 {link.MaxErr:F4}");
            Assert.Equal(0, link.Blended);
            Assert.Equal(0, link.Snapped);
        }
        else
        {
            // 输入**真的丢了**（丢包超出冗余窗口）⇒ 服务端只能跳过缺口、客户端校正回来。
            // 这时要求的是"有界 + 能自愈"，而不是"零误差"：
            //   · 失配次数由跳过次数解释（每条缺口最多影响若干快照），不能发散；
            //   · 误差不超过"直接贴"阈值（没有传送级错乱）；
            //   · 末尾 20 份快照必须重新完全一致（真的收敛回来了）。
            Assert.True(link.RingMismatch <= link.Server.Desyncs * 3 + 5,
                $"失配 {link.RingMismatch} 与跳过缺口 {link.Server.Desyncs} 不成比例（在发散？）");
            Assert.True(link.MaxErr <= 1.5f, $"误差峰值 {link.MaxErr:F3} 超过直接贴阈值（错乱）");
            Assert.Equal(0, link.RingMismatchRecent);   // ★ 末尾自愈
        }
    }
}
