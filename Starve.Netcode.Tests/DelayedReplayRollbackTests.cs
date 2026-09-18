using Starve.Netcode;

namespace Starve.Netcode.Tests;

/// <summary>
/// 延迟重放 / 延迟回退 / **微小差距**的成体系对抗用例（组件层，不启动 Godot）。
///
/// 与 <c>NetcodeAdversarialLinkTests</c> 的分工：那边跑"整条链路 + 真实 OwnMovePredictor"，
/// 这边把时钟**钉成精确映射**（<see cref="NetcodeConfig.ClockMaxStepMs"/> = 0，
/// 且首份快照准时到达 ⇒ <c>TickAt(wall) == wall/50</c>），于是每一步都能手算、
/// 或者用一条"从不被校正的参考链"对拍。只有这样才能把下面这些细节钉死：
/// <list type="bullet">
/// <item>重放用的到底是**每条 tick 当时**的意图，还是"最新的那条"（当初 keepLast=0 的 bug）；</item>
/// <item>重放有没有覆盖**小数 tick**（活推进是逐帧的，漏掉就每份快照差半格）；</item>
/// <item>回退发生在过渡中间时，接缝是不是**零跳变**（残差被重新播种，而不是叠加）；</item>
/// <item>死区附近的 0.001 格差异会不会被当成"需要平滑的失配"而永久微抖。</item>
/// </list>
/// </summary>
public sealed class DelayedReplayRollbackTests
{
    private const long Tick0 = 1000;
    private const double TickMs = 50.0;
    private const float Speed = 10f;      // 格/秒 ⇒ 每 tick 固定 0.5 格
    private const float SimStep = 0.5f;
    private const ulong Epoch = 7;

    private static long WallOf(double tick) => (long)Math.Round(tick * TickMs);
    private static FakeAction A(int dx) => new() { Dx = dx };

    /// <summary>精确时钟：偏移由首份（准时的）快照锚定，之后一次都不再动。</summary>
    private static NetcodeConfig ExactClock => new() { ClockMaxStepMs = 0 };

    private static NetSnapshot<FakeState> Snap(long tick, float x, ulong seq = 0, ulong epoch = Epoch) =>
        new(tick, seq, epoch, new FakeState { X = x }, false);

    /// <summary>建链（1000 处 X=0）并保持精确时钟。</summary>
    private static ClientSmoother<FakeState, FakeAction> New(NetcodeConfig? config = null)
    {
        var smoother = new ClientSmoother<FakeState, FakeAction>(
            new FakeModel { Speed = Speed }, config ?? ExactClock);
        smoother.OnSnapshot(Snap(Tick0, 0f), WallOf(Tick0));
        return smoother;
    }

    private sealed class CountingMetrics : INetcodeMetrics
    {
        public readonly List<CorrectionKind> Kinds = new();
        public readonly List<CorrectionReport> Reports = new();
        public int Resyncs;

        public int Snapshots => Reports.Count;
        public int Blended => Kinds.Count(k => k == CorrectionKind.Blended);
        public int Snapped => Kinds.Count(k => k == CorrectionKind.Snapped);
        public int Deadzone => Kinds.Count(k => k == CorrectionKind.Deadzone);
        public int Stale => Kinds.Count(k => k == CorrectionKind.Stale);
        public int Clamped => Reports.Count(r => r.ReplayClamped);

        public void OnCorrection(in CorrectionReport report)
        {
            Kinds.Add(report.Kind);
            Reports.Add(report);
        }

        public void OnClockResync(long serverTick, long nowMs, double offsetMs) => Resyncs++;
    }

    // ═══════════════════════════ 1. 延迟重放 ═══════════════════════════

    /// <summary>
    /// 意图时间线（<b>窗口内会转三次向</b>），用来把"重放用了哪条意图"彻底暴露出来：
    /// <code>
    /// tick  : 1000 1001 1002 1003 1004 1005 1006 1007 1008 1009 1010
    /// 意图  : +1   +1   +1   -1   -1   -1    0    0    0    0   +1（1010 起才换）
    /// 位置  : 0.0  0.5  1.0  1.5  1.0  0.5  0.0 -0.5 -0.5 -0.5 -0.5
    /// </code>
    /// 所以只要"整段重放都用最新意图"或"漏掉一 tick / 多算一 tick"，末态立刻对不上。
    /// </summary>
    private static readonly (double Tick, int Dx)[] TurningTimeline =
    {
        (1000, 1), (1003, -1), (1006, 0), (1010, 1),
    };

    [Theory]
    [InlineData(0, 2)]
    [InlineData(0, 3)]
    [InlineData(2, 3)]
    [InlineData(6, 3)]
    [InlineData(6, 6)]
    [InlineData(9, 9)]
    [InlineData(14, 2)]
    [InlineData(19, 3)]   // 19 tick = 950ms，刚好在上限内
    [InlineData(20, 3)]   // 20 tick = 上限本身：`wanted > Max` 才是限幅 ⇒ 这里不该限
    [InlineData(25, 3)]   // 25 tick：超上限 ⇒ 限幅，客户端稳定落后
    public void DelayedReplayOfATruePastStateReproducesTheLocalChainExactly(int delayTicks, int snapTickOffset)
    {
        var subject = New();
        var oracle = New();                    // 参考链：同样意图、从不被校正
        ulong seq = 0;
        var sched = 0;

        var snapTick = Tick0 + snapTickOffset;
        var deliverTick = snapTick + delayTicks;
        var maxReplay = subject.Config.MaxReplayTicks;
        var clamped = delayTicks > maxReplay;
        // 客户端是按帧往前走的：这条链路里它已经到过 deliverTick，
        // 所以重放终点 = max(限幅终点, 已经到过的时刻) = deliverTick（见 ClientSmoother 里的说明）。
        var expectTick = deliverTick;

        float authX = float.NaN, oracleAtX = float.NaN;
        for (var tick = Tick0 + 1; tick <= deliverTick; tick++)
        {
            // 意图按"它本来该发出的墙钟时刻"记录 ⇒ 生效 tick 精确等于时间线上的 tick
            while (sched < TurningTimeline.Length && TurningTimeline[sched].Tick <= tick)
            {
                var (at, dx) = TurningTimeline[sched++];
                seq++;
                subject.SetIntent(seq, Epoch, A(dx), WallOf(at));
                oracle.SetIntent(seq, Epoch, A(dx), WallOf(at));
            }

            subject.AdvanceTo(tick);
            oracle.AdvanceTo(tick);

            if (float.IsNaN(authX) && oracle.StateTick == snapTick)
            {
                authX = oracle.State.X;        // "服务端在 snapTick 那一刻的真实位置"
            }

            if (float.IsNaN(oracleAtX) && oracle.StateTick == expectTick)
            {
                oracleAtX = oracle.State.X;    // 同一时刻本地链应该在的位置
            }
        }

        Assert.False(float.IsNaN(authX), "测试自身有问题：没取到快照时刻的权威值");
        Assert.False(float.IsNaN(oracleAtX), "测试自身有问题：没取到目标时刻的参考值");

        // 迟到的权威快照：内容是"过去某一 tick 的真实状态"
        var report = subject.OnSnapshot(
            Snap(snapTick, authX), WallOf(deliverTick));

        Assert.Equal(clamped, report.ReplayClamped);
        // 重放长度 = "客户端已经预测到哪"，所以即使 wanted 超过上限也不会倒退回去重放
        Assert.Equal(delayTicks, report.ReplayedTicks);
        Assert.Equal(expectTick, subject.StateTick);

        // ★ 核心：延迟重放必须**逐位复现**本地链（同样的意图 + 同样的 tick 长）
        Assert.Equal(oracleAtX, subject.State.X, 5);

        // ★ 而且不能产生"假失配"：重放到同一时刻，两者本来就该相等
        Assert.Equal(CorrectionKind.Deadzone, report.Kind);
        Assert.True(report.Err <= subject.Config.CorrectionDeadzone,
            $"延迟 {delayTicks} tick 的重放产生了假误差 {report.Err:F4}（本该为 0）");
    }

    [Fact]
    public void DelayedReplayUsesTheHistoricalIntentInsideTheWindow()
    {
        var s = New();
        s.SetIntent(1, Epoch, A(1), WallOf(1000));   // 1000 起 +X
        s.SetIntent(2, Epoch, A(-1), WallOf(1003)); // 1003 起 -X
        s.SetIntent(3, Epoch, A(0), WallOf(1006));  // 1006 起松手（最新意图！）
        s.AdvanceTo(1009);

        // 服务端在 1003 说 X=1.5；这份快照晚到 6 tick（现在 = 1009）
        var report = s.OnSnapshot(Snap(1003, 1.5f), WallOf(1009));

        // 重放 1003,1004,1005 = 三步 -1 ⇒ 0.0；1006..1008 三步 0 ⇒ 停在 0.0
        // 若整段用了"最新意图"（0 = 松手），结果会是 1.5，差 1.5 格 ——
        // 这正是当初 keepLast=0 把历史裁光后的实测症状（误差随延迟线性增长）。
        Assert.Equal(CorrectionKind.Deadzone, report.Kind);
        Assert.Equal(0.0f, s.State.X, 4);
        Assert.Equal(1009, s.StateTick);
    }

    /// <summary>
    /// <c>Acknowledge</c> 必须留够历史：重放窗口用到的意图<b>往往已经被服务端确认</b>了。
    /// 这里窗口内第一条 tick（1004）要用的意图（seq1，生效 1000）就在被裁的范围内。
    /// （把 <c>keepLast</c> 改回 0，本用例立刻失败：整段重放会回落到最新意图 -1，
    /// 末态变成 -0.5 而不是 0.5。）
    /// </summary>
    [Fact]
    public void AckedInputsAreStillAvailableToTheReplayWindow()
    {
        var s = New();
        s.SetIntent(1, Epoch, A(1), WallOf(1000));
        s.SetIntent(2, Epoch, A(-1), WallOf(1005));
        s.AdvanceTo(1009);

        // tick 1004 的权威真值 = 2.0；appliedSeq=2 表示服务端把两条输入都烘进去了
        var report = s.OnSnapshot(Snap(1004, 2.0f, seq: 2), WallOf(1009));

        // 重放 1004（旧意图 +1 ⇒ 2.5）…1005..1008（-1 × 4 ⇒ 0.5）
        Assert.Equal(0.5f, s.State.X, 4);
        Assert.Equal(1009, s.StateTick);
        // 历史留得住 ⇒ 重放逐位复现本地链 ⇒ 同刻误差为 0（连死区都不该越过）
        Assert.Equal(CorrectionKind.Deadzone, report.Kind);
        Assert.Equal(0f, report.Err, 5);
    }

    /// <summary>
    /// 活推进是**逐帧**的（每帧跨 1.x tick），重放只走到整 tick 就会每次少走一小截
    /// ——实测表现是"每份快照差半格"。这条把小数 tick 钉死。
    /// </summary>
    [Fact]
    public void FractionalLiveAdvanceIsReplayedTooNotJustWholeTicks()
    {
        var s = New();
        s.SetIntent(1, Epoch, A(1), WallOf(1000));
        s.AdvanceTo(1006.4);                         // 6.4 tick × 0.5 = 3.2
        Assert.Equal(3.2f, s.State.X, 4);

        // 权威给出 1006 这一刻的真值 3.0；现在时刻是 1006.4
        var report = s.OnSnapshot(Snap(1006, 3.0f), WallOf(1006.4));

        Assert.Equal(1, report.ReplayedTicks);        // 小数也算"重放过"，不是 0
        Assert.Equal(3.2f, s.State.X, 4);             // 3.0 + 0.4×0.5
        Assert.Equal(CorrectionKind.Deadzone, report.Kind);
        Assert.Equal(0f, report.Err, 5);
    }

    /// <summary>限幅边界必须正好卡在 <c>wanted &gt; MaxReplayTicks</c> 上（多一 tick 就限）。</summary>
    [Fact]
    public void ReplayClampBoundaryIsExact()    {
        var config = ExactClock with { MaxReplayTicks = 5 };

        static float AuthAt(long tick) => (tick - Tick0) * SimStep;

        var ok = New(config);
        ok.SetIntent(1, Epoch, A(1), WallOf(Tick0));
        ok.AdvanceTo(1002);
        var r1 = ok.OnSnapshot(Snap(1002, AuthAt(1002)), WallOf(1007)); // wanted = 5 = 上限
        Assert.False(r1.ReplayClamped);
        Assert.Equal(5, r1.ReplayedTicks);
        Assert.Equal(1007, ok.StateTick);

        var clamped = New(config);
        clamped.SetIntent(1, Epoch, A(1), WallOf(Tick0));
        clamped.AdvanceTo(1002);
        var r2 = clamped.OnSnapshot(Snap(1002, AuthAt(1002)), WallOf(1008)); // wanted = 6 > 上限
        Assert.True(r2.ReplayClamped);
        Assert.Equal(5, r2.ReplayedTicks);
        Assert.Equal(1007, clamped.StateTick);   // 停在"快照 + 上限"，而不是"现在"
        Assert.Equal(ok.State.X, clamped.State.X, 5);
    }

    /// <summary>
    /// 延迟尖峰把重放窗口顶到上限之外时，**不许把已经预测的时间拉回去**。
    /// 老版本会把终点截在"快照 + 上限"（这里 = 1010），而客户端已经走到 1012：
    /// 于是 predicted 推不动（不能倒退）、重放却停在 1010 ⇒ 误差在两个不同时刻之间比较
    /// ⇒ 位置明明完全一致，却报出 1.0 格的假回退（把链从 6.0 拉到 5.0）。
    /// </summary>
    [Fact]
    public void DelaySpikeBeyondTheCapDoesNotRewindAlreadyPredictedTime()
    {
        var config = ExactClock with { MaxReplayTicks = 3 };
        var s = New(config);
        s.SetIntent(1, Epoch, A(1), WallOf(Tick0));
        s.AdvanceTo(1012);                                   // 客户端按帧预测到 6.0

        var before = s.State.X;
        // 服务端在 1007 的真实位置 3.5；这份快照晚到 5 tick（wanted = 5 > 上限 3）
        var report = s.OnSnapshot(Snap(1007, 3.5f), WallOf(1012));

        Assert.True(report.ReplayClamped);
        Assert.Equal(1012, s.StateTick);                      // ★ 不倒退
        Assert.Equal(5, report.ReplayedTicks);                // 重放到"已经到过的时刻"
        Assert.Equal(before, s.State.X, 5);                   // ★ 链头位置完全没变（权威=本地预测）
        Assert.Equal(0f, report.Err, 5);                      // ★ 没有假失配
        Assert.Equal(CorrectionKind.Deadzone, report.Kind);   // 更不该判成"直接贴"
        Assert.Equal(s.State.X, s.RenderedState.X, 5);
    }

    /// <summary>正向限幅仍然照旧：客户端还没走到"快照 + 上限"时，重放就停在上限。</summary>
    [Fact]
    public void ForwardReplayIsStillCappedWhenTheClientHasNotCaughtUp()
    {
        var config = ExactClock with { MaxReplayTicks = 3 };
        var s = New(config);
        s.SetIntent(1, Epoch, A(1), WallOf(Tick0));

        // 只靠快照驱动（客户端没有额外按帧推进）：链头停在"上一份的终点"
        var r1 = s.OnSnapshot(Snap(1004, 2.0f), WallOf(1006));    // wanted = 2 ≤ 3
        Assert.False(r1.ReplayClamped);
        Assert.Equal(1006, s.StateTick);

        var r2 = s.OnSnapshot(Snap(1005, 2.5f), WallOf(1012));    // wanted = 7 > 3
        Assert.True(r2.ReplayClamped);
        Assert.Equal(3, r2.ReplayedTicks);                        // 停在上限，剩下的靠后续快照追上
        Assert.Equal(1008, s.StateTick);
    }

    // ═══════════════════════════ 2. 延迟回退 ═══════════════════════════

    /// <summary>回退到"更旧的位置"、而且这份快照本身还晚到了一段：屏幕一点都不能跳。</summary>
    [Fact]
    public void LateRollbackHasZeroSeamJumpAndConverges()
    {
        var s = New();
        s.SetIntent(1, Epoch, A(1), WallOf(1000));
        s.AdvanceTo(1010);                        // 预测到 5.0
        var seam = s.RenderedState.X;

        // 权威在 1008 只承认 3.6（中途被顶了一下），这份快照还晚了 4 tick
        var report = s.OnSnapshot(Snap(1008, 3.6f), WallOf(1012));

        Assert.Equal(seam, s.RenderedState.X, 5);          // ★ 接缝零跳变
        Assert.Equal(CorrectionKind.Blended, report.Kind); // 差 0.4 < 1.5 ⇒ 平滑
        Assert.Equal(5.6f, s.State.X, 4);                  // 3.6 + 4×0.5：模拟立刻是权威+重放

        s.AdvanceTo(1015);                                 // 过渡 3 tick
        Assert.False(s.Blending);
        Assert.Equal(s.State.X, s.RenderedState.X, 5);      // ★ 必然收敛，不留残余
        Assert.Equal(7.1f, s.State.X, 4);
    }

    /// <summary>
    /// 回退打在**过渡中间**（真实链路就是这样，残差还没消化完下一份就来了）：
    /// 第二次的接缝也必须零跳变 —— 残差要从"当前屏幕位置"重新播种，而不是叠加/清零。
    /// </summary>
    [Fact]
    public void RollbackMidBlendReseedsFromTheScreenWithoutJumping()
    {
        var s = New();
        s.SetIntent(1, Epoch, A(1), WallOf(1000));
        s.AdvanceTo(1005);
        s.AdvanceTo(1006);                                     // 屏幕 3.0

        var seam1 = s.RenderedState.X;
        var r1 = s.OnSnapshot(Snap(1005, 2.0f), WallOf(1006));  // 权威少 0.5
        Assert.Equal(CorrectionKind.Blended, r1.Kind);
        Assert.Equal(seam1, s.RenderedState.X, 5);              // ★ 第一次接缝
        Assert.True(s.Blending);

        s.AdvanceTo(1007);                                      // 过渡只走了一部分
        Assert.True(s.Blending);                                // 确实还在过渡中间

        var seam2 = s.RenderedState.X;
        var r2 = s.OnSnapshot(Snap(1006, 2.4f), WallOf(1007));  // 还在往回拉
        Assert.Equal(CorrectionKind.Blended, r2.Kind);
        Assert.Equal(seam2, s.RenderedState.X, 5);              // ★ 第二次接缝（不叠加、不清零）
        Assert.Equal(0.1f, r2.Err, 4);

        s.AdvanceTo(1012);
        Assert.False(s.Blending);
        Assert.Equal(s.State.X, s.RenderedState.X, 5);          // ★ 最终必然收敛
    }

    /// <summary>
    /// 链路永久超出重放上限（超出规格的延迟）时，客户端应当退化成
    /// "稳定地落后 (延迟 − 上限) tick"，而**不是**每份快照都判一次"直接贴"的风暴。
    /// 这是设计行为（文档里要写清楚），不是 bug —— 但必须有测试锁住它。
    /// </summary>
    [Fact]
    public void RepeatedClampedRollbacksDegradeToAConstantLagNotASnapStorm()
    {
        var config = ExactClock with { MaxReplayTicks = 10 };
        var s = New(config);
        var m = new CountingMetrics();
        s.Metrics = m;
        s.SetIntent(1, Epoch, A(1), WallOf(Tick0));

        const int lagTicks = 15;   // 750ms 延迟，超出 10 tick 上限
        // 注意不要额外 AdvanceTo(现在)：真实客户端每帧推进的目标本身就被限幅后的
        // 重放终点带着走（客户端稳定落后 (lag − 上限) tick），这里让组件自己推。
        for (var tick = Tick0 + lagTicks + 1; tick <= 1060; tick++)
        {
            var snapTick = tick - lagTicks;
            var auth = (snapTick - Tick0) * SimStep;        // 服务端一直匀速走
            var report = s.OnSnapshot(Snap(snapTick, auth), WallOf(tick));

            Assert.True(report.ReplayClamped, "超出上限的延迟必须被记成限幅");
            Assert.Equal(config.MaxReplayTicks, report.ReplayedTicks);   // 正向重放仍然被上限卡住
            Assert.NotEqual(CorrectionKind.Snapped, report.Kind);   // 限幅 ≠ 每次直接贴
        }

        // 服务端现在在 1060，客户端稳定停在 1060 − (15−10) = 1055
        Assert.Equal(1055, s.StateTick);
        Assert.Equal((1055 - Tick0) * SimStep, s.State.X, 3);
        Assert.Equal(0, m.Snapped);
        Assert.Equal(0, m.Blended);
        Assert.True(m.Clamped > 20, "测试应该真的撞了很多次上限");
    }

    /// <summary>乱序（迟到）的回退包：只吃它的 ack，绝不把已经预测到前面的链拖回去。</summary>
    [Fact]
    public void OutOfOrderRollbackIsStaleAndNeverMovesThePlayerBackwards()
    {
        var s = New();
        s.SetIntent(1, Epoch, A(1), WallOf(1000));
        s.AdvanceTo(1010);

        var r1 = s.OnSnapshot(Snap(1012, 4.6f), WallOf(1014));   // 新的：往回拉 1.4 格
        Assert.Equal(CorrectionKind.Blended, r1.Kind);

        var state = s.State.X;
        var rendered = s.RenderedState.X;
        var version = s.StateVersion;

        // 一份更旧的包（模拟严重乱序/重传延迟）：必须被丢弃
        var r2 = s.OnSnapshot(Snap(1005, 0.0f), WallOf(1014));

        Assert.Equal(CorrectionKind.Stale, r2.Kind);
        Assert.Equal(state, s.State.X);                   // 链一动不动
        Assert.Equal(rendered, s.RenderedState.X);
        Assert.Equal(version, s.StateVersion);            // 陈旧包连版本都不该动
    }

    /// <summary>同一 tick 的重传（内容一致）必须是彻底的空操作：不清过渡、屏幕不动。</summary>
    [Fact]
    public void SameTickDuplicateIsANoOpAndDoesNotClearTheBlend()
    {
        var s = New();
        s.SetIntent(1, Epoch, A(1), WallOf(1000));
        s.AdvanceTo(1006);                                 // 屏幕 3.0

        var r1 = s.OnSnapshot(Snap(1005, 2.0f), WallOf(1006));
        Assert.Equal(CorrectionKind.Blended, r1.Kind);
        Assert.True(s.Blending);

        var remaining = s.BlendTicksRemaining;
        var rendered = s.RenderedState.X;

        var r2 = s.OnSnapshot(Snap(1005, 2.0f), WallOf(1006));   // 同一份重传

        Assert.Equal(CorrectionKind.Deadzone, r2.Kind);
        Assert.True(s.Blending, "重传不能把正在进行的过渡清掉（那会是一次可见突跳）");
        Assert.Equal(remaining, s.BlendTicksRemaining);
        Assert.Equal(rendered, s.RenderedState.X, 5);
    }

    /// <summary>同一 tick 但内容被服务端改了（更小的修正）：重新起过渡，接缝仍然零跳变。</summary>
    [Fact]
    public void SameTickCorrectionRestartsTheBlendFromTheScreen()
    {
        var s = New();
        s.SetIntent(1, Epoch, A(1), WallOf(1000));
        s.AdvanceTo(1006);

        s.OnSnapshot(Snap(1005, 2.0f), WallOf(1006));
        var seam = s.RenderedState.X;

        var r = s.OnSnapshot(Snap(1005, 2.45f), WallOf(1006));   // 差只有 0.05
        Assert.Equal(CorrectionKind.Blended, r.Kind);
        Assert.Equal(seam, s.RenderedState.X, 5);                // ★ 零跳变
        Assert.True(s.Blending);
    }

    /// <summary>传送级的延迟回退：恰好一次"直接贴"，之后必须回到正常（不再贴）。</summary>
    [Fact]
    public void BigLateRollbackSnapsExactlyOnceThenFollows()
    {
        var s = New();
        var m = new CountingMetrics();
        s.Metrics = m;
        s.SetIntent(1, Epoch, A(1), WallOf(1000));
        s.AdvanceTo(1010);                                  // 预测 5.0

        var snap = s.OnSnapshot(Snap(1008, 1.5f), WallOf(1011));   // 差 2.5 ⇒ 直接贴
        Assert.Equal(CorrectionKind.Snapped, snap.Kind);
        Assert.Equal(s.State.X, s.RenderedState.X, 5);             // 设计上不掩饰
        Assert.Equal(3.0f, s.State.X, 4);                          // 1.5 + 3×0.5

        // 之后服务端从这个新位置继续正常走：不该再有一次"直接贴"
        for (var tick = 1012; tick <= 1030; tick++)
        {
            s.AdvanceTo(tick);
            var auth = 1.5f + (tick - 1008) * SimStep;
            var report = s.OnSnapshot(Snap(tick, auth), WallOf(tick));
            Assert.NotEqual(CorrectionKind.Snapped, report.Kind);
        }

        Assert.Equal(1, m.Snapped);
        Assert.False(s.Blending);
        Assert.Equal(s.State.X, s.RenderedState.X, 5);
    }

    /// <summary>大坐标（关卡存档 tick 很大、世界坐标很大）下的微小回退也必须收敛、不 NaN。</summary>
    [Fact]
    public void MicroRollbackAtLargeCoordinatesStillConverges()
    {
        var s = New();
        s.SetIntent(1, Epoch, A(1), WallOf(1000));
        s.OnSnapshot(Snap(1000, 1_000_000f), WallOf(1000));   // 先跳到大坐标
        Assert.Equal(1_000_000f, s.State.X);

        s.AdvanceTo(1005);
        s.AdvanceTo(1006);                                     // 屏幕 1_000_003.0
        var seam = s.RenderedState.X;

        var report = s.OnSnapshot(Snap(1005, 1_000_002f), WallOf(1006));

        Assert.Equal(CorrectionKind.Blended, report.Kind);
        Assert.Equal(seam, s.RenderedState.X, 3);               // 接缝零跳变（大坐标下精度更低）
        Assert.False(float.IsNaN(report.Err));

        s.AdvanceTo(1011);
        Assert.False(s.Blending);
        Assert.Equal(s.State.X, s.RenderedState.X, 3);
        Assert.True(float.IsFinite(s.State.X));
    }

    /// <summary>一帧里补来一串快照（乱序 + 最新混在一起）：末态只能由最新那份决定。</summary>
    [Fact]
    public void SnapshotBurstInOneFrameKeepsTheChainAndNeverJumps()
    {
        var s = New();
        var m = new CountingMetrics();
        s.Metrics = m;
        s.SetIntent(1, Epoch, A(1), WallOf(1000));
        s.AdvanceTo(1010);

        var before = s.RenderedState.X;
        var r1 = s.OnSnapshot(Snap(1008, 3.5f), WallOf(1012)); // 差 0.5
        Assert.Equal(before, s.RenderedState.X, 5);            // 每份都不许跳

        var mid = s.RenderedState.X;
        var r2 = s.OnSnapshot(Snap(1006, 3.0f), WallOf(1012)); // 更旧 ⇒ 陈旧
        Assert.Equal(CorrectionKind.Stale, r2.Kind);
        Assert.Equal(mid, s.RenderedState.X);

        var mid2 = s.RenderedState.X;
        var r3 = s.OnSnapshot(Snap(1011, 4.5f), WallOf(1012)); // 最新
        Assert.Equal(mid2, s.RenderedState.X, 5);

        Assert.Equal(CorrectionKind.Blended, r1.Kind);
        Assert.Equal(CorrectionKind.Blended, r3.Kind);
        Assert.Equal(5.0f, s.State.X, 4);                      // 4.5 @1011 → 重放到 1012
        Assert.Equal(1012, s.StateTick);
        Assert.True(s.State.X > 4.0f, "中间那份旧包绝不能把链拖回去");
        Assert.Equal(1, m.Stale);
        Assert.Equal(3, m.Snapshots);
    }

    // ═══════════════════════════ 3. 微小差距 ═══════════════════════════

    [Theory]
    [InlineData(0f)]
    [InlineData(1e-7f)]
    [InlineData(1e-5f)]
    [InlineData(1e-4f)]
    [InlineData(1e-3f)]
    [InlineData(0.01f)]
    [InlineData(0.02f)]
    [InlineData(0.0299f)]
    public void SubDeadzoneDiscrepancyIsSwallowedWithoutBlending(float delta)
    {
        var s = New();
        var m = new CountingMetrics();
        s.Metrics = m;
        s.SetIntent(1, Epoch, A(1), WallOf(1000));
        s.AdvanceTo(1006);
        var before = s.RenderedState.X;

        var report = s.OnSnapshot(Snap(1006, 3.0f - delta), WallOf(1006));

        Assert.Equal(CorrectionKind.Deadzone, report.Kind);
        Assert.True(report.Err <= s.Config.CorrectionDeadzone,
            $"误差 {report.Err} 应该落在死区 {s.Config.CorrectionDeadzone} 内");
        Assert.False(s.Blending);
        Assert.Equal(0f, s.Residual.X, 6);
        Assert.Equal(s.State.X, s.RenderedState.X, 6);
        Assert.Equal(1, m.Snapshots);
        // 死区的语义就是"直接对齐这一点点，看不见"：位移必须有界，不能变成突跳
        Assert.True(MathF.Abs(s.RenderedState.X - before) <= s.Config.CorrectionDeadzone + 1e-5f);
    }

    /// <summary>
    /// 判据是<b>严格小于</b>死区才算噪声。用 2 的幂（0.03125 = 2⁻⁵）当死区，
    /// 让"差多少"在 float 里精确可表示 —— 否则边界会被浮点舍入吃掉（实测 3.0−0.03f 就落在死区内）。
    /// </summary>
    [Fact]
    public void DiscrepancyExactlyAtTheDeadzoneBlends()
    {
        var dz = 0.03125f;                                    // 2⁻⁵
        var config = ExactClock with { CorrectionDeadzone = dz };

        var at = New(config);
        at.SetIntent(1, Epoch, A(1), WallOf(1000));
        at.AdvanceTo(1006);
        var report = at.OnSnapshot(Snap(1006, 3.0f - dz), WallOf(1006));
        Assert.Equal(dz, report.Err, 6);                      // 精确等于死区
        Assert.Equal(CorrectionKind.Blended, report.Kind);
        Assert.True(at.Blending);

        var inside = New(config);
        inside.SetIntent(1, Epoch, A(1), WallOf(1000));
        inside.AdvanceTo(1006);
        var r2 = inside.OnSnapshot(Snap(1006, 3.0f - (dz - 0.0078125f)), WallOf(1006));   // 差 2⁻⁷
        Assert.Equal(CorrectionKind.Deadzone, r2.Kind);
        Assert.False(inside.Blending);
    }

    /// <summary>五百次"死区内的噪声"不能累积成漂移或永久微抖。</summary>
    [Fact]
    public void FiveHundredSubDeadzoneJittersLeaveNoResidualOrDrift()
    {
        var s = New();
        s.SetIntent(1, Epoch, A(1), WallOf(1000));
        var rng = new Random(5);
        var maxDeviation = 0f;

        for (var i = 0; i < 500; i++)
        {
            var tick = 1001 + i;
            s.AdvanceTo(tick);
            var auth = s.State.X + (float)(rng.NextDouble() * 0.05 - 0.025);   // ±0.025 < 死区

            var report = s.OnSnapshot(Snap(tick, auth), WallOf(tick));

            Assert.Equal(CorrectionKind.Deadzone, report.Kind);
            Assert.False(s.Blending);
            maxDeviation = MathF.Max(maxDeviation, MathF.Abs(s.State.X - auth));
        }

        Assert.True(maxDeviation < 1e-4f, $"死区内的噪声被放大了：{maxDeviation}");
        Assert.Equal(0f, s.Residual.X, 6);
        Assert.Equal(s.State.X, s.RenderedState.X, 6);
        Assert.Equal(1500, s.StateTick);
        Assert.True(s.StateVersion > 500);
    }

    /// <summary>
    /// 刚过死区的"微小回退"：必须走平滑，且过渡时长/速度都在规格内
    /// （时长 = max(残差/速度上限, 下限)，每 tick 修正量 ≤ 速度上限×dt）。
    /// </summary>
    [Fact]
    public void MicroscopicRollbackConvergesWithCappedSpeedAndMinimumDuration()
    {
        var s = New();
        s.SetIntent(1, Epoch, A(1), WallOf(1000));
        s.AdvanceTo(1006);
        s.AdvanceTo(1007);                       // 屏幕 3.5

        var report = s.OnSnapshot(Snap(1006, 3.031f), WallOf(1007));

        Assert.Equal(CorrectionKind.Blended, report.Kind);
        Assert.True(report.Err > 0.03f && report.Err < 1.5f);
        Assert.True(report.BlendTicks >= 3, $"过渡至少要有下限 3 tick，实际 {report.BlendTicks}");
        Assert.Equal(s.BlendTicksTotal, report.BlendTicks);
        Assert.Equal(s.BlendTicksRemaining, report.BlendTicks);

        var total = s.BlendTicksTotal;
        var cap = s.Config.MaxCorrectionSpeed * s.Config.TickSeconds;
        var previous = s.RenderedState.X;
        for (var i = 0; i < total + 2; i++)
        {
            s.AdvanceTo(s.StateTick + 1);
            var step = MathF.Abs(s.RenderedState.X - previous);
            Assert.True(step <= SimStep + (float)cap + 1e-3f,
                $"第 {i} 步修正 {step:F4} 超过上限 {SimStep + cap:F4}");
            previous = s.RenderedState.X;
        }

        Assert.False(s.Blending);
        Assert.Equal(s.State.X, s.RenderedState.X, 5);
    }

    // ═══════════════════════════ 4. 指标出口 ═══════════════════════════

    /// <summary>
    /// 三类报告（含死区与陈旧）都要**恰好一次**地送到业务实现的接口上 ——
    /// 业务拿它写 jsonl 日志；漏掉任何一类，"为什么这次看不见"就查不出来。
    /// </summary>
    [Fact]
    public void EveryCorrectionKindIsReportedToMetricsExactlyOnce()
    {
        var s = new ClientSmoother<FakeState, FakeAction>(new FakeModel { Speed = Speed }, ExactClock);
        var m = new CountingMetrics();
        s.Metrics = m;

        s.OnSnapshot(Snap(Tick0, 0f), WallOf(Tick0));            // ① Bootstrap
        s.SetIntent(1, Epoch, A(1), WallOf(Tick0));
        s.AdvanceTo(1005);
        s.OnSnapshot(Snap(1005, 2.0f), WallOf(1006));            // ② Blended（差 0.5）
        s.OnSnapshot(Snap(1005, 2.0f), WallOf(1006));            // ③ Deadzone（同 tick 重传）
        s.OnSnapshot(Snap(1004, 1.5f), WallOf(1006));            // ④ Stale（比最新还旧）

        Assert.Equal(
            new[] { CorrectionKind.Bootstrap, CorrectionKind.Blended, CorrectionKind.Deadzone, CorrectionKind.Stale },
            m.Kinds);
        Assert.Equal(4, m.Snapshots);
        Assert.Equal(0.5f, m.Reports[1].Err, 4);
        Assert.Equal(0, m.Resyncs);
    }
}
