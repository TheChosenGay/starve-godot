using Starve.Core;
using Starve.Netcode;

namespace Starve.Core.Tests;

/// <summary>
/// 对抗性链路测试（不启动 Godot）：显式控制每份快照的**投递时机**，
/// 把"延迟重放 / 延迟回退 / 微小差距"这些情况一个个摆出来，
/// 每一帧都跑同一组**不变量**。
///
/// 为什么必须成体系地测：这套东西单看某一帧"好像对"说明不了问题 ——
/// 之前就是"误差指标算错"在 happy path 下完全看不出来（恒定意图下误差恒为 0.5 格）。
/// </summary>
public sealed class NetcodeAdversarialLinkTests
{
    private const long TickBase = 1000;
    private const double TickMs = 50.0;
    private const float Speed = 10f;      // 格/秒
    private const int ClientFps = 60;
    private static readonly double FrameMs = 1000.0 / ClientFps;

    /// <summary>一条可控链路：服务端按 tick 跑，快照按指定延迟投递。</summary>
    private sealed class Link
    {
        private readonly List<(long At, long Tick, OwnMoveState State, ulong Seq)> _inFlight = new();
        private readonly OwnMovePredictor _server;
        private readonly List<string> _violations = new();
        private int _maxReplay;

        public readonly ClientSmoother<OwnMoveState, MoveIntent> Smoother;
        public readonly Collector Metrics = new();
        public OwnMoveState ServerState = OwnMoveState.FromContinuous(0f, 0f);
        public MoveIntent ServerIntent = new(1, 0);
        public ulong ServerAppliedSeq;
        public long ServerTick = TickBase;
        public long Wall;
        public ulong ClientSeq;
        public MoveIntent CurrentIntent = new(1, 0);
        private long _nextResendAt;
        private readonly List<(long At, MoveIntent Intent, ulong Seq)> _inputInFlight = new();

        /// <summary>下一份快照的投递延迟（毫秒）；测试可随时改它来制造延迟变化。</summary>
        public long DelayMs = 100;

        /// <summary>
        /// 可选：按 tick 动态决定下行（快照）投递延迟，用于制造"延迟尖峰"。
        /// 只作用于下行：上行（输入）仍用 <see cref="DelayMs"/>，
        /// 这样"尖峰"就是纯粹的突发缓冲，而不是两端一起抽风。
        /// </summary>
        public Func<long>? DownstreamDelaySource;

        public int Dropped;
        public bool DropEveryThird;
        private int _published;

        private float _previousRendered;
        private float _previousState;
        private uint _lastVersion;
        private long _renderedFrames;
        private bool _jumpAllowed;

        public Link()
        {
            _server = new OwnMovePredictor((_, _) => true);
            _server.SetSpeed(Speed);
            var client = new OwnMovePredictor((_, _) => true);
            client.SetSpeed(Speed);
            Smoother = new ClientSmoother<OwnMoveState, MoveIntent>(client, new NetcodeConfig());
            Smoother.Metrics = Metrics;
            Metrics.OffsetSource = () => Smoother.Clock.OffsetMs;
            _maxReplay = Smoother.Config.MaxReplayTicks;
            _lastVersion = Smoother.StateVersion;
        }

        public void ServerAdvance(int ticks)
        {
            for (var i = 0; i < ticks; i++)
            {
                _server.Step(ref ServerState, ServerIntent, TickMs / 1000.0, 0);
                ServerTick++;
                var delay = DownstreamDelaySource?.Invoke() ?? DelayMs;
                var at = (long)((ServerTick - TickBase) * TickMs) + delay;
                _published++;
                if (DropEveryThird && _published % 3 == 0)
                {
                    Dropped++;
                    continue;
                }

                _inFlight.Add((at, ServerTick, ServerState, ServerAppliedSeq));
            }
        }

        /// <summary>把服务端状态瞬移一段（模拟传送/击退）。</summary>
        public void ServerTeleport(float dx) =>
            ServerState = OwnMoveState.FromContinuous(ServerState.X + dx, ServerState.Y);

        public void ClientIntent(MoveIntent intent)
        {
            CurrentIntent = intent;
            ClientSeq++;
            // 客户端记录的"生效 tick"= 发送时的服务端 tick 估计 + 当前生效延迟估计
            var effective = Smoother.Clock.TickAt(Wall) + Smoother.EstimatedInputDelayTicks;
            IntentTiming.Add((ClientSeq, intent, effective, 0));
            IntentDetail[ClientSeq] = ($"发@wall{Wall} delay={DelayMs} 时钟tick={Smoother.Clock.TickAt(Wall):F1}" +
                                      $" 生效延迟估计={Smoother.EstimatedInputDelayTicks:F2} 计划到达@wall{Wall + DelayMs}");
            Smoother.SetIntent(ClientSeq, 1, intent, Wall);
            // 输入走同一条单程延迟送到服务端（真实链路两个方向都延迟）
            _inputInFlight.Add((Wall + DelayMs, intent, ClientSeq));
        }

        /// <summary>
        /// 每条输入的"客户端以为的生效 tick" vs"服务端真正用上它的 tick"（诊断用）。
        /// 这个差就是**意图时间线误差**：换向时会被放大成 2×误差tick×0.5 格的位置差。
        /// </summary>
        public readonly List<(ulong Seq, MoveIntent Intent, double Effective, long AppliedAt)> IntentTiming = new();
        public readonly Dictionary<ulong, string> IntentDetail = new();

        public string IntentTimingText(int count = 5)
        {
            var worst = IntentTiming
                .Where(t => t.AppliedAt != 0 && t.Effective > 0)   // effective=0 = 时钟还没同步那几条，不参与
                .Select(t => (t.Seq, t.Intent, t.Effective, t.AppliedAt, Error: t.AppliedAt - t.Effective))
                .OrderByDescending(t => Math.Abs(t.Error))
                .Take(count)
                .Select(t => $"seq{t.Seq}({t.Intent.Dx:+0;-0;0}) 以为生效@{t.Effective:F1} 实际@{t.AppliedAt}" +
                             $" 差{t.Error:+0.0;-0.0;0}tick [{IntentDetail.GetValueOrDefault(t.Seq, "?")} 服务端此刻@{t.AppliedAt}]");
            return string.Join("\n    ", worst);
        }

        public void Frames(int count)
        {
            for (var i = 0; i < count; i++)
            {
                Wall = (long)Math.Round((_renderedFrames + i + 1) * FrameMs);

                // 投递到期的输入
                for (var k = _inputInFlight.Count - 1; k >= 0; k--)
                {
                    if (_inputInFlight[k].At > Wall) continue;
                    ServerIntent = _inputInFlight[k].Intent;
                    ServerAppliedSeq = _inputInFlight[k].Seq;
                    var seq = _inputInFlight[k].Seq;
                    for (var t = 0; t < IntentTiming.Count; t++)
                    {
                        if (IntentTiming[t].Seq != seq) continue;
                        IntentTiming[t] = (seq, IntentTiming[t].Intent, IntentTiming[t].Effective, ServerTick + 1);
                        break;
                    }

                    _inputInFlight.RemoveAt(k);
                }

                // 投递到期的快照（可能是多份 —— 突发场景）
                var snapFrame = _renderedFrames + i + 1;
                // 同一帧可能补来多份快照：只要**任一**份是"不掩饰"的，本帧就允许跳
                // （后面那份若是 Stale，会把标志覆盖掉 —— 踩过）。
                for (var k = _inFlight.Count - 1; k >= 0; k--)
                {
                    if (_inFlight[k].At > Wall) continue;
                    var m = _inFlight[k];
                    // 重放长度上界：要么被 MaxReplayTicks 卡住，要么只重放到"客户端已经预测到的地方"
                    // （校正绝不会把时间倒回去，见 ClientSmoother 里的说明）。
                    var tickBeforeSnapshot = Smoother.StateTick;
                    var report = Smoother.OnSnapshot(
                        new NetSnapshot<OwnMoveState>(m.Tick, m.Seq, 1, m.State, false), Wall);
                    var allowedReplay = Math.Max(_maxReplay,
                        (int)Math.Max(0, tickBeforeSnapshot - report.SnapshotTick + 1));
                    if (report.ReplayedTicks > allowedReplay)
                    {
                        _violations.Add(
                            $"重放 {report.ReplayedTicks} tick 超过上界 {allowedReplay}" +
                            $"（快照 {report.SnapshotTick}，重放前链头 {tickBeforeSnapshot}）");
                    }

                    if (report.Kind is CorrectionKind.Snapped or CorrectionKind.Bootstrap)
                        _jumpAllowed = true;
                    _inFlight.RemoveAt(k);
                }

                // 真实客户端每 100ms 重发一次当前意图（换 seq，方向不变）。
                // 这既是真实行为，也是"单程延迟"标定的唯一来源 —— 少了它，
                // 客户端会整整慢一个延迟（实测末态误差恰好 = 延迟/100 格）。
                if (snapFrame == 1) ClientIntent(new MoveIntent(1, 0));
                if (Wall >= _nextResendAt)
                {
                    _nextResendAt = Wall + 100;
                    ClientIntent(CurrentIntent);
                }

                Smoother.AdvanceFrame(Wall);
                CheckFrame();
            }
        }

        /// <summary>推进 N 秒：服务端 20Hz 与客户端帧同步推进。</summary>
        public void Run(double seconds)
        {
            var ticks = (int)Math.Round(seconds * 1000 / TickMs);
            var frames = (int)Math.Round(seconds * 1000 / FrameMs);
            var done = 0;
            for (var f = 0; f < frames; f++)
            {
                var targetTick = (int)((f + 1) * FrameMs / TickMs);
                if (targetTick > done) { ServerAdvance(targetTick - done); done = targetTick; }
                Frames(1);
            }
        }

        private void CheckFrame()
        {
            _renderedFrames++;
            var x = Smoother.RenderedState.X;
            if (!float.IsFinite(x) || !float.IsFinite(Smoother.State.X))
                _violations.Add($"第 {_renderedFrames} 帧出现 NaN/Inf");

            if (!_jumpAllowed)
            {
                // 上界按**本帧实际推进量**成比例：快照帧可能一次推进好几个 tick
                // （重放），拿"一帧固定上界"去卡会误报。
                // 语义：渲染位移 ≤ 模拟位移 × (1 + 校正上限/移动速度)。
                var simStep = MathF.Abs(Smoother.State.X - _previousState);
                // 校正量 ≤ 收敛速度 × 本帧推进的时长；而"推进的时长"至少是一帧
                // （静止时 tick 照样在走，残差照样在衰减）。
                var advancedSeconds = Math.Max(simStep / Speed, FrameMs / 1000.0);
                var bound = simStep
                            + (float)(Smoother.Config.MaxCorrectionSpeedStopped * advancedSeconds)
                            + 0.02f;
                var step = MathF.Abs(x - _previousRendered);
                if (step > bound)
                {
                    var r = Smoother.LastReport;
                    _violations.Add(
                        $"第 {_renderedFrames} 帧位移 {step:F4}（模拟 {Smoother.State.X - _previousState:F4}）> {bound:F4}" +
                        $" [kind={r.Kind} replay={r.ReplayedTicks} clamped={r.ReplayClamped}" +
                        $" snapTick={r.SnapshotTick} stateTick={r.StateTick} oneWay={Smoother.EstimatedOneWayTicks:F2}]");
                }
            }

            if (Smoother.StateVersion < _lastVersion)
                _violations.Add($"第 {_renderedFrames} 帧链版本倒退");
            _maxReplay = Smoother.Config.MaxReplayTicks;
            _lastVersion = Smoother.StateVersion;

            _previousRendered = x;
            _previousState = Smoother.State.X;
            _jumpAllowed = false;
        }

        public void AssertNoViolations(string because)
        {
            Assert.True(_violations.Count == 0,
                $"{because}：违反不变量 {_violations.Count} 次，前几条：\n  " +
                string.Join("\n  ", _violations.Take(5)));
        }

        public double Error => Math.Abs(Smoother.State.X - (ServerState.X));
    }

    private sealed class Collector : INetcodeMetrics
    {
        public int Snapshots, Stale, Deadzone, Blended, Snapped, Clamped, Starved, Resyncs;
        public int ReplayMax, ReplayTotal, ReplayCount;
        public double MaxOffsetStep;
        public double LastOffset = double.NaN;
        public double ErrMax;

        public Func<double>? OffsetSource;

        public void OnCorrection(in CorrectionReport r)
        {
            Snapshots++;
            var offset = OffsetSource is null ? double.NaN : OffsetSource();
            if (!double.IsNaN(offset) && !double.IsNaN(LastOffset))
                MaxOffsetStep = Math.Max(MaxOffsetStep, Math.Abs(offset - LastOffset));
            if (!double.IsNaN(offset)) LastOffset = offset;
            switch (r.Kind)
            {
                case CorrectionKind.Stale: Stale++; break;
                case CorrectionKind.Deadzone: Deadzone++; break;
                case CorrectionKind.Blended: Blended++; break;
                case CorrectionKind.Snapped: Snapped++; Snaps.Add(r); break;
            }

            if (r.ReplayedTicks > 0) { ReplayCount++; ReplayTotal += r.ReplayedTicks; ReplayMax = Math.Max(ReplayMax, r.ReplayedTicks); }
            if (r.ReplayClamped) Clamped++;
            if (r.Starved) Starved++;
            ErrMax = Math.Max(ErrMax, r.Err);
            if (r.Err >= 1.0f) Big.Add(r);   // ≥1 格的都留档：排查"为什么这一下很卡"
        }

        /// <summary>误差 ≥ 1 格的所有和解（诊断用，正常应当为空）。</summary>
        public readonly List<CorrectionReport> Big = new();

        /// <summary>所有"直接贴"（含"残差超上限"从平滑改判过来的）。</summary>
        public readonly List<CorrectionReport> Snaps = new();

        public string SnapsText() => Snaps.Count == 0
            ? "-"
            : string.Join(" | ", Snaps.Take(6).Select(r =>
                $"@{r.SnapshotTick}→{r.StateTick} err={r.Err:F2} 重放={r.ReplayedTicks}" +
                $" 限幅={(r.ReplayClamped ? 1 : 0)} 过渡={r.BlendTicks}"));

        public string WorstText() => Big.Count == 0
            ? "无 ≥1 格的和解"
            : string.Join(" | ", Big.Take(6).Select(r =>
                $"{r.Kind}@{r.SnapshotTick}→{r.StateTick} err={r.Err:F2} 重放={r.ReplayedTicks}" +
                $" 预测步={r.PredictedSteps} 预测速={r.PredictedStepSpeed:F1} 权威速={r.AuthoritativeStepSpeed:F1}" +
                $" 限幅={(r.ReplayClamped ? 1 : 0)} 断流={(r.Starved ? 1 : 0)}")) +
              (Big.Count > 6 ? $" …共 {Big.Count} 次" : "");

        /// <summary>所有 ≥1 格和解的快照 tick（看它们是"开局爬升期"还是"稳态"）。</summary>
        public string BigTicks() => Big.Count == 0 ? "-" : string.Join(",", Big.Select(r => r.SnapshotTick));

        /// <summary>
        /// 清零计数 —— 用来**跳过开局爬升期**：单程延迟估计是限速逼近的
        /// （每次快照最多 ±0.5 tick，这是为了避免开局时间轴突跳），
        /// 头一两秒的换向必然带着 1~2 tick 的时间线误差。稳态才是要判定的东西。
        /// </summary>
        public void Reset()
        {
            Snapshots = Stale = Deadzone = Blended = Snapped = Clamped = Starved = Resyncs = 0;
            ReplayMax = ReplayTotal = ReplayCount = 0;
            ErrMax = 0;
            Big.Clear();
            Snaps.Clear();
        }

        public void OnClockResync(long serverTick, long nowMs, double offsetMs) => Resyncs++;

        /// <summary>时钟同步之前 OffsetMs 无意义，第一次直接认下来。</summary>
        public void PrimeOffset(double offset) => LastOffset = offset;

        public string Summary() =>
            $"快照 {Snapshots}（接受 {Deadzone} 平滑 {Blended} 直接贴 {Snapped} 陈旧 {Stale} 限幅 {Clamped}）" +
            $" 重放均 {(ReplayCount == 0 ? 0 : (double)ReplayTotal / ReplayCount):F1}/最大 {ReplayMax}" +
            $" 误差峰值 {ErrMax:F2} 重同步 {Resyncs}";
    }

    // ───────────────── 延迟重放 ─────────────────

    [Fact]
    public void UniformDeliveryReplaysAndStaysExact()
    {
        var link = new Link { DelayMs = 100 };
        link.Run(6);
        Console.WriteLine($"均匀 100ms | {link.Metrics.Summary()} | 末态误差 {link.Error:F4}");
        link.AssertNoViolations("均匀投递");
        Assert.True(link.Metrics.ReplayCount > 0, "应该发生重放");
        Assert.True(link.Error < 0.6, $"末态误差过大 {link.Error:F3}");
    }

    [Theory]
    [InlineData(200)]   // 4 tick
    [InlineData(500)]   // 10 tick
    [InlineData(900)]   // 18 tick，超过 MaxReplayTicks
    [InlineData(2000)]  // 40 tick，极端
    public void LargeDeliveryDelayIsClampedAndSurvives(int delayMs)
    {
        var link = new Link { DelayMs = delayMs };
        link.Run(6);
        Console.WriteLine($"延迟 {delayMs,4}ms | {link.Metrics.Summary()} | 末态误差 {link.Error:F4}");
        link.AssertNoViolations($"延迟 {delayMs}ms");
        Assert.True(link.Metrics.ReplayMax <= link.Smoother.Config.MaxReplayTicks, "重放必须被 MaxReplayTicks 限住");
        Assert.True(float.IsFinite(link.Smoother.State.X), "极端延迟下不得出现 NaN");
    }

    [Fact]
    public void DelayDropsFromHugeToNormalAndConverges()
    {
        var link = new Link { DelayMs = 1500 }; // 30 tick：重放被限幅
        link.Run(3);
        var duringStall = link.Error;

        link.DelayMs = 60; // 突然变好
        link.Run(12); // 单程估计是限速逼近的，需要几秒才能把 30 tick 拉回 1 tick

        Console.WriteLine($"延迟 1500→60ms | {link.Metrics.Summary()} | 期间误差 {duringStall:F2} 末态 {link.Error:F4}");
        link.AssertNoViolations("延迟突变");
        Assert.True(link.Error < 0.6, $"延迟恢复后没收敛：{link.Error:F3}");
    }

    [Fact]
    public void DroppedSnapshotsDoNotBreakTheChain()
    {
        var link = new Link { DelayMs = 100, DropEveryThird = true };
        link.Run(6);
        Console.WriteLine($"丢 1/3 | {link.Metrics.Summary()} | 丢 {link.Dropped} 份 | 末态误差 {link.Error:F4}");
        link.AssertNoViolations("丢包");
        Assert.True(link.Dropped > 10, "测试应该真的丢了包");
        Assert.True(link.Error < 1.0, $"丢包后误差过大 {link.Error:F3}");
    }

    // ───────────────── 延迟回退 ─────────────────

    [Fact]
    public void TeleportIsASingleSnapThenConverges()
    {
        var link = new Link { DelayMs = 100 };
        link.Run(2);
        link.ServerTeleport(8f); // 传送 8 格
        link.Run(3);

        Console.WriteLine($"传送 8 格 | {link.Metrics.Summary()} | 末态误差 {link.Error:F4}");
        link.AssertNoViolations("传送");
        Assert.Equal(1, link.Metrics.Snapped); // 恰好一次"直接贴"，之后回到正常
        Assert.True(link.Error < 0.6, $"传送后没收敛：{link.Error:F3}");
    }

    [Fact]
    public void BackwardsDeliveryOfAnOldSnapshotIsReportedStaleAndIgnored()
    {
        var link = new Link { DelayMs = 100 };
        link.Run(2);
        var staleBefore = link.Metrics.Stale;

        // 人为塞一份"很久以前"的快照（模拟严重乱序）
        var oldState = OwnMoveState.FromContinuous(link.Smoother.State.X - 5f, link.Smoother.State.Y);
        var report = link.Smoother.OnSnapshot(
            new NetSnapshot<OwnMoveState>(link.ServerTick - 30, 1, 1, oldState, false), link.Wall);

        Assert.Equal(CorrectionKind.Stale, report.Kind);
        Assert.True(link.Metrics.Stale > staleBefore, "迟到包必须被记成陈旧");
        link.AssertNoViolations("乱序");
    }

    [Fact]
    public void RepeatedBackwardsCorrectionsDoNotDiverge()
    {
        // 服务端一直比客户端"慢半拍"（两端世界真相持续有差异）：
        // 客户端必须一直被拉回，但**不能越来越偏**。
        var link = new Link { DelayMs = 100 };
        link.Run(2);
        var errors = new List<double>();
        for (var i = 0; i < 10; i++)
        {
            link.ServerTeleport(-0.35f);
            link.Run(0.5);
            errors.Add(link.Error);
        }

        Console.WriteLine($"持续往回拉 | {link.Metrics.Summary()} | 误差 {string.Join(", ", errors.Select(e => e.ToString("F2")))}");
        link.AssertNoViolations("持续回退");
        Assert.True(errors[^1] <= errors[0] + 0.3, $"误差在累积：{errors[0]:F2} → {errors[^1]:F2}");
    }

    // ───────────────── 抖动 / 时钟 ─────────────────

    [Fact]
    public void JitterDoesNotMakeTheClockJumpOrResync()
    {
        var link = new Link { DelayMs = 200 };
        var rng = new Random(7);
        for (var i = 0; i < 200; i++)
        {
            link.DelayMs = 200 + rng.Next(-60, 61); // ±60ms 抖动
            link.Run(0.05);
        }

        Console.WriteLine($"±60ms 抖动 | {link.Metrics.Summary()} | 单次最大偏移变化 {link.Metrics.MaxOffsetStep:F2}ms");
        link.AssertNoViolations("抖动");
        Assert.Equal(0, link.Metrics.Resyncs);           // 抖动不该触发时间基准重建
        Assert.True(link.Metrics.MaxOffsetStep <= 10.01, // ClockMaxStepMs = 10
            $"时钟单次跳变 {link.Metrics.MaxOffsetStep:F2}ms 超过限速");
    }

    [Fact]
    public void StressRandomLatencyJitterAndLossKeepsEveryInvariant()
    {
        var link = new Link { DelayMs = 120 };
        var rng = new Random(20260917);
        for (var i = 0; i < 120; i++)
        {
            link.DelayMs = rng.Next(20, 320);
            link.DropEveryThird = rng.Next(20) == 0; // 偶发丢一阵
            if (rng.Next(40) == 0) link.ServerTeleport(rng.Next(-3, 4));
            if (rng.Next(30) == 0) link.ClientIntent(new MoveIntent(rng.Next(-1, 2), rng.Next(-1, 2)));
            link.Run(0.5);
        }

        Console.WriteLine($"压力（随机延迟 20~320ms + 丢包 + 传送 + 换向，60s）| {link.Metrics.Summary()}" +
                          $" | 单次最大偏移变化 {link.Metrics.MaxOffsetStep:F2}ms | 末态误差 {link.Error:F3}");
        link.AssertNoViolations("压力");
        Assert.True(float.IsFinite(link.Smoother.State.X), "压力下不得出现 NaN");
        Assert.True(link.Metrics.ReplayMax <= link.Smoother.Config.MaxReplayTicks, "压力下重放也必须被限幅");
    }

    /// <summary>
    /// 下行延迟尖峰（突发缓冲填满 → 快照晚到 24 tick &gt; MaxReplayTicks=20），持续 2 秒再恢复。
    ///
    /// 关键：位置本来是**一致**的，只是到达得晚。老版本会把重放终点截回"快照 + 上限"，
    /// 而客户端已经按帧预测到更前面 ⇒ 误差在两个不同时刻之间比较 ⇒ 把一次纯延迟
    /// 误判成"直接贴"，还会把已经算好的预测白扔（可见突跳）。这里锁住修复后的行为。
    /// </summary>
    [Fact]
    public void DownstreamDelaySpikeBeyondTheReplayCapDoesNotCauseAVisibleJump()
    {
        var link = new Link { DelayMs = 100 };
        link.Run(4);

        var spike = true;
        link.DownstreamDelaySource = () => spike ? 1200 : 100;   // 24 tick，超出上限
        link.Run(2);
        var duringSpike = link.Metrics.Summary();

        spike = false;
        link.Run(8);

        Console.WriteLine($"下行尖峰 100→1200→100ms | 尖峰期 {duringSpike}");
        Console.WriteLine($"                          恢复后 {link.Metrics.Summary()} | 末态误差 {link.Error:F3}");
        Console.WriteLine($"  ≥1 格：{link.Metrics.WorstText()}");
        Console.WriteLine($"  直接贴：{link.Metrics.SnapsText()}");
        link.AssertNoViolations("下行延迟尖峰");
        Assert.True(link.Metrics.Clamped > 10, "尖峰期应该大量触发限幅（这正是超出规格的延迟）");
        Assert.Equal(0, link.Metrics.Snapped);   // ★ 纯延迟尖峰不该产生任何"直接贴"
        Assert.True(link.Error < 0.8, $"尖峰恢复后没收敛：{link.Error:F3}");
    }

    /// <summary>
    /// 服务端持续做**亚死区**（每次 0.02 格）的微调：客户端应当直接对齐、不做平滑、
    /// 也绝不判成失配 —— 否则屏幕会永久微抖（这是"微小差距"最典型的坏结果）。
    /// </summary>
    [Fact]
    public void SubDeadzoneServerDriftNeverTriggersASmoothOrSnap()
    {
        var link = new Link { DelayMs = 100 };
        link.Run(3);
        var blendedBefore = link.Metrics.Blended;

        for (var i = 0; i < 20; i++)
        {
            link.ServerTeleport(-0.02f);   // 2 厘米，远小于死区 0.03
            link.Run(0.5);
        }

        Console.WriteLine($"亚死区漂移 -0.02×20 | {link.Metrics.Summary()} | 末态误差 {link.Error:F3}");
        link.AssertNoViolations("亚死区漂移");
        Assert.Equal(0, link.Metrics.Snapped);
        Assert.Equal(blendedBefore, link.Metrics.Blended);   // 一次都不该起过渡
    }

    /// <summary>回退包本身还带着抖动（到达时机不固定）：既不突跳，误差也不累积。</summary>
    [Fact]
    public void JitteredRollbacksNeverJumpAndDoNotAccumulate()
    {
        var link = new Link { DelayMs = 150 };
        link.Run(3);

        var rng = new Random(99);
        var errors = new List<double>();
        for (var i = 0; i < 30; i++)
        {
            link.ServerTeleport(-0.25f);                  // 持续被往回拉
            link.DelayMs = 150 + rng.Next(-100, 101);     // 拉回的通知到达时机抖动
            link.Run(0.4);
            errors.Add(link.Error);
        }

        Console.WriteLine($"抖动回退 -0.25×30 | {link.Metrics.Summary()}" +
                          $" | 误差 {errors.Min():F2}~{errors.Max():F2}");
        link.AssertNoViolations("抖动回退");
        Assert.True(errors[^1] <= errors[0] + 0.5, $"误差在累积：{errors[0]:F2} → {errors[^1]:F2}");
        Assert.True(link.Metrics.Snapped <= 1, $"回退不该反复退化成直接贴：{link.Metrics.Snapped}");
    }

    /// <summary>高频换向 + 抖动 + 偶发丢包：意图时间线是最容易被搞错的地方。</summary>
    /// <summary>
    /// 中等抖动（±20ms）+ 每 400ms 换向 + 偶发丢包：**稳态必须干净**。
    /// 先跑 3 秒爬升期（单程延迟估计是限速逼近的，头几秒的换向必然带时间线误差），
    /// 清零计数之后再判定 —— 这样"启动代价"和"稳态质量"不会混在一起看。
    /// </summary>
    [Fact]
    public void FrequentTurnsUnderModerateJitterAreCleanInSteadyState()
    {
        var link = new Link { DelayMs = 200 };
        var rng = new Random(4242);

        void Phase(double seconds, int jitter)
        {
            var steps = (int)Math.Round(seconds / 0.2);
            for (var i = 0; i < steps; i++)
            {
                link.DelayMs = 200 + rng.Next(-jitter, jitter + 1);
                link.DropEveryThird = rng.Next(20) == 0;
                if (i % 2 == 0) link.ClientIntent(new MoveIntent(rng.Next(2) == 0 ? 1 : -1, 0));
                link.Run(0.2);
            }
        }

        Phase(3, 20);                       // 爬升期：只用来让延迟估计收敛
        var warmup = link.Metrics.Summary();
        link.Metrics.Reset();
        link.IntentTiming.Clear();
        link.IntentDetail.Clear();
        Phase(15, 20);                      // 稳态

        Console.WriteLine("200±20ms 抖动 + 每 400ms 换向 + 偶发丢包");
        Console.WriteLine($"  爬升期（3s）| {warmup}");
        Console.WriteLine($"  稳态（15s） | {link.Metrics.Summary()} | 末态误差 {link.Error:F3}");
        Console.WriteLine($"             | ≥1 格：{link.Metrics.WorstText()}");
        Console.WriteLine($"             | ≥1 格快照 tick：{link.Metrics.BigTicks()}");
        Console.WriteLine($"             | 意图时间线最差：{link.IntentTimingText(3)}");

        link.AssertNoViolations("中等抖动换向");
        // 换向的整 tick 量化：客户端把转向放在 ceil(发送 tick + 延迟估计)，
        // 服务端放在 ceil(到达 tick)，两者差不到半 tick 也会整整数差 1 tick
        // （反转时放大成 1.0 格）。没有服务端回传的"生效 tick"，这就是地板；
        // 但它落在"平滑"档里 ⇒ 屏幕上只是走得略快一点，不该出现"直接贴"。
        Assert.Equal(0, link.Metrics.Snapped);
        Assert.True(link.Metrics.ErrMax <= 1.2f, $"稳态误差峰值 {link.Metrics.ErrMax:F2} 偏大");
        Assert.True(link.Metrics.MaxOffsetStep <= 10.01, "时钟单次跳变超过限速");
        Assert.True(float.IsFinite(link.Smoother.State.X));
    }

    /// <summary>
    /// 【已知问题 · 有界】延迟在 120~280ms 之间每 200ms 方波抖动 + 偶发丢包 + 每 400ms 换向时，
    /// 稳态仍会出现 ~1 格的和解（实测最多 2.9 tick 的意图时间线误差）。
    ///
    /// 根因已定位（见下面的"意图时间线最差"输出）：输入的**生效 tick 是在发送时刻**用当时的
    /// 延迟估计冻结下来的，而单程延迟在抖动时无法在发送时刻预知 —— 换向的时间线最多差 ~3 tick，
    /// 方向反转会把位置差放大成 2×3×0.5 ≈ 1 格以上。
    /// <para>
    /// 根治办法是**服务端回传"最后一条输入是在哪一 tick 生效的"**（<c>last_applied_seq</c> +
    /// 生效 tick）：客户端就能精确锚定，而不是拿抖动中的延迟去猜。
    /// 本用例把当前量级锁住 —— 既不假装没问题，也不让回归变得更糟。
    /// 注意"直接贴"必须是 0：1 格级别的失配落在平滑档里，屏幕上只是走得略快一点。
    /// </para>
    /// </summary>
    [Fact]
    public void SevereLatencyJitterProducesVisibleButBoundedCorrections()
    {
        var link = new Link { DelayMs = 200 };
        var rng = new Random(4242);

        void Phase(double seconds)
        {
            var steps = (int)Math.Round(seconds / 0.2);
            for (var i = 0; i < steps; i++)
            {
                link.DelayMs = 200 + rng.Next(-80, 81);
                link.DropEveryThird = rng.Next(10) == 0;
                if (i % 2 == 0) link.ClientIntent(new MoveIntent(rng.Next(2) == 0 ? 1 : -1, 0));
                link.Run(0.2);
            }
        }

        Phase(4);
        link.Metrics.Reset();
        link.IntentTiming.Clear();
        link.IntentDetail.Clear();
        Phase(18);

        Console.WriteLine("200±80ms 方波抖动 + 每 400ms 换向 + 偶发丢包（已知问题，有界）");
        Console.WriteLine($"  稳态（18s） | {link.Metrics.Summary()}");
        Console.WriteLine($"             | ≥1 格：{link.Metrics.WorstText()}");
        Console.WriteLine($"             | ≥1 格快照 tick：{link.Metrics.BigTicks()}");
        Console.WriteLine($"             | 意图时间线最差：{link.IntentTimingText(2)}");

        link.AssertNoViolations("严重抖动换向");
        Assert.True(link.Metrics.ErrMax <= 1.5f, $"误差峰值 {link.Metrics.ErrMax:F2} 超出已知量级");
        Assert.Equal(0, link.Metrics.Snapped);   // ★ 1 格级失配必须落在平滑档，不能变可见突跳
        Assert.True(link.Metrics.Blended > 0, "这种抖动下本来就该有平滑校正");
        Assert.True(float.IsFinite(link.Smoother.State.X));
    }

}
