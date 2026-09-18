using Starve.Netcode;

namespace Starve.Netcode.Tests;

/// <summary>
/// 【序号锚定和解】的测试：**每个 tick 一条编号操作 + 存"执行完它之后我自己的状态"**，
/// 和解时直接比"同一个操作序号之后的两个状态"。
///
/// 要钉住的语义（这几条旧路径做不到）：
/// <list type="number">
/// <item><b>输入延迟 ≈ 0</b>：按下方向的**下一个 tick** 就生效，与在途延迟估计无关；</item>
/// <item><b>同序号直接可比</b>：我执行完第 N 条的状态 == 服务端执行完第 N 条的状态（同一前缀、同样 tick 数）；</item>
/// <item><b>在途操作不制造假误差</b>：我已经多跑了 k 条，服务端还停在 N —— 比的是"N 之后"，
///   所以差 0、链一动不动（旧路径在这里会按"服务端的钟"硬对齐，逼出输入延迟）；</item>
/// <item><b>真分歧才校正</b>：服务端在 N 处的状态和我不同（撞墙/被顶开/算错）才 rebase + 用我的节拍重跑。</item>
/// </list>
/// </summary>
public sealed class SequenceAnchoredReconciliationTests
{
    private const long Tick0 = 1000;
    private const ulong Epoch = 7;
    private const float Speed = 10f;      // 0.5 格/tick

    private static long WallOf(double tick) => (long)Math.Round(tick * 50.0);
    private static FakeAction A(int dx) => new() { Dx = dx };

    private static NetcodeConfig Indexed => new() { ClockMaxStepMs = 0, TickIndexedIntents = true };

    private static NetSnapshot<FakeState> Snap(long tick, float x, ulong appliedSeq) =>
        new(tick, appliedSeq, Epoch, new FakeState { X = x }, false);

    private static ClientSmoother<FakeState, FakeAction> New(NetcodeConfig? config = null)
    {
        var s = new ClientSmoother<FakeState, FakeAction>(new FakeModel { Speed = Speed }, config ?? Indexed);
        s.OnSnapshot(Snap(Tick0, 0f, 0), WallOf(Tick0));
        return s;
    }

    /// <summary>一个"每 tick 消费一条操作"的假服务端：和客户端跑同一套数学、同一条操作流。</summary>
    private sealed class TickServer
    {
        private readonly FakeModel _model = new() { Speed = Speed };
        public FakeState State = new();
        public ulong Applied;
        public int Consumed;

        /// <summary>消费第 <paramref name="index"/> 条操作（一条 = 一个 tick）。</summary>
        public void Consume(ulong index, FakeAction action)
        {
            _model.Step(ref State, action, 0.05, 0);
            Applied = index;
            Consumed++;
        }
    }

    [Fact]
    public void InputTakesEffectOnTheVeryNextTick()
    {
        var s = New();
        s.SetIntent(1, Epoch, A(1), WallOf(Tick0));
        s.AdvanceTo(Tick0 + 3);
        Assert.Equal(1.5f, s.State.X, 4);

        // 按下反向：下一个 tick 就该反向（序号锚定模式下不掺任何"在途延迟估计"）
        s.SetIntent(2, Epoch, A(-1), WallOf(Tick0 + 3));
        s.AdvanceTo(Tick0 + 4);
        Assert.Equal(1.0f, s.State.X, 4);
        s.AdvanceTo(Tick0 + 5);
        Assert.Equal(0.5f, s.State.X, 4);
    }

    [Fact]
    public void MyStateAfterOpNIsStoredAndComparable()
    {
        var s = New();
        s.SetIntent(1, Epoch, A(1), WallOf(Tick0));
        s.AdvanceTo(Tick0 + 5);

        // 每个 tick 一条操作：序号是**自增计数器**（与 tick 无关）；第 i 条 = 第 i 个 tick 步
        Assert.True(s.TryGetOpState(1, out var after1));
        Assert.Equal(0.5f, after1.X, 4);
        Assert.True(s.TryGetOpState(5, out var after5));
        Assert.Equal(2.5f, after5.X, 4);
        Assert.False(s.TryGetOpState(99, out _));
    }

    /// <summary>
    /// 核心：两边跑同一条操作流（每 tick 一条），服务端晚 3 tick 消费。
    /// 客户端拿"服务端已执行到第 N 条 + 它执行完的状态"和"我自己执行完第 N 条的状态"比 → 差 0。
    /// **注意此时客户端已经多跑了 3 条**（在途），却完全不该产生误差。
    /// </summary>
    [Fact]
    public void SameOpIndexIsExactEvenWhileLaterOpsAreInFlight()
    {
        var s = New();
        var server = new TickServer();
        const int delay = 3;

        s.SetIntent(1, Epoch, A(1), WallOf(Tick0));
        var stream = new List<(ulong Index, FakeAction Action)>();
        var pending = new Queue<FakeAction>();

        for (var tick = Tick0 + 1; tick <= Tick0 + 8; tick++)
        {
            if (tick == Tick0 + 4) s.SetIntent(2, Epoch, A(-1), WallOf(tick));   // 中途反向
            var action = tick >= Tick0 + 4 ? A(-1) : A(1);
            pending.Enqueue(action);                                            // 上行：先发出
            s.AdvanceTo(tick);                                                  // 本地立刻生效（下一个 tick 步）
            stream.Add((s.CurrentOpIndex, action));                             // 客户端这一步发出去的序号

            // 服务端每 tick 消费一条"到期"的操作（晚 delay 个 tick）
            if (stream.Count <= delay) continue;
            var due = stream[stream.Count - 1 - delay];
            server.Consume(due.Index, due.Action);
        }

        // 客户端已经跑到 1008；服务端只执行到第 5 条（在途 3 条）
        Assert.Equal(5UL, server.Applied);
        Assert.Equal(Tick0 + 8, s.StateTick);

        var before = s.State.X;
        var report = s.OnSnapshot(Snap(Tick0 + 9, server.State.X, server.Applied), WallOf(Tick0 + 9));

        Assert.True(s.TryGetOpState(server.Applied, out var mine));
        Assert.Equal(mine.X, server.State.X, 5);            // ★ 同一序号之后的状态本来就相等
        Assert.Equal(CorrectionKind.Deadzone, report.Kind); // ★ 没有误差
        Assert.Equal(0f, report.Err, 5);
        Assert.Equal(0, report.ReplayedTicks);
        Assert.Equal(before, s.State.X, 5);                 // ★ 链一动不动（在途操作不制造假校正）
    }

    /// <summary>服务端在同一个序号处的状态和我不同 → 这才是真分歧：才校正，而且用我自己的节拍重跑。</summary>
    [Fact]
    public void RealDivergenceAtTheSameOpIndexIsCorrected()
    {
        var s = New();
        s.SetIntent(1, Epoch, A(1), WallOf(Tick0));
        s.AdvanceTo(Tick0 + 6);                    // 我跑到 1006（x = 3.0）
        var before = s.State.X;

        // 服务端说"我执行到第 3 条"（= 从 tick 1002 起步的那一步），但它那里的状态比我的少 0.8
        Assert.True(s.TryGetOpState(3, out var mine));
        var serverX = mine.X - 0.8f;
        var report = s.OnSnapshot(Snap(Tick0 + 4, serverX, 3), WallOf(Tick0 + 6));

        Assert.Equal(0.8f, report.Err, 4);          // 真分歧 = 同序号处的差
        Assert.Equal(CorrectionKind.Blended, report.Kind);
        Assert.True(report.ReplayedTicks >= 3, $"应该从第 3 条之后重跑到链头，实际 {report.ReplayedTicks}");
        Assert.Equal(Tick0 + 6, s.StateTick);       // 链头 tick 不变（时间不倒流）
        // 校正后的链头 = 服务端那个状态 + 重跑的 3 条（每条 0.5）
        Assert.Equal(serverX + 1.5f, s.State.X, 4);
        Assert.NotEqual(before, s.State.X);

        s.AdvanceTo(Tick0 + 12);                    // 过渡收完（正好落在整 tick 上）
        Assert.False(s.Blending);
        // ⚠️ 默认渲染是"上一条 tick → 最新 tick"之间插值（延迟一个 tick），
        //    所以整 tick 落点上渲染=**上一条** tick 的状态，而不是 State 本身。
        Assert.Equal(s.State.X - 0.5f, s.RenderedState.X, 4);
    }

    /// <summary>序号已经不在回看范围里（延迟超长）→ 不能瞎比，退回旧路径（并保持可用）。</summary>
    [Fact]
    public void AckOutsideTheHistoryFallsBackToTheLegacyPath()
    {
        var s = new NetcodeConfig { ClockMaxStepMs = 0, TickIndexedIntents = true, OpHistoryCapacity = 4 };
        var smoother = New(s);
        smoother.SetIntent(1, Epoch, A(1), WallOf(Tick0));
        smoother.AdvanceTo(Tick0 + 20);            // 环里只剩最近 4 条

        var report = smoother.OnSnapshot(
            Snap(Tick0 + 20, smoother.State.X, 2), WallOf(Tick0 + 20));

        Assert.True(smoother.StateTick >= Tick0 + 20, "不该把链拉回去");
        Assert.True(float.IsFinite(report.Err));
        Assert.True(report.ReplayedTicks <= smoother.Config.MaxReplayTicks + 1);
    }

    /// <summary>环里存的"操作之后的状态"必须是**我自己的**推进结果，与快照内容无关。</summary>
    [Fact]
    public void StoredStatesComeFromMyOwnSimulationNotFromSnapshots()
    {
        var s = New();
        s.SetIntent(1, Epoch, A(1), WallOf(Tick0));
        s.AdvanceTo(Tick0 + 3);
        Assert.True(s.TryGetOpState(3, out var before));

        // 一份"服务端同意"的快照：不该改任何东西
        s.OnSnapshot(Snap(Tick0 + 3, before.X + 0.5f, 3), WallOf(Tick0 + 3));

        Assert.True(s.TryGetOpState(3, out var after));
        Assert.Equal(before.X, after.X, 5);
    }

    /// <summary>
    /// 一个 tick 里按多次 → 只产出**一条**编号操作（取该 tick 内最后一次意图）。
    ///
    /// 这是"服务端每 tick 消费一条"能成立的前提：玩家可以按得比 tick 快（100Hz 也行），
    /// 客户端代他按 tick 采样 ⇒ 操作流上界 = 20 条/秒 = 服务端消费速率 ⇒ 队列永不积压。
    /// 代价：同一 tick 内的多次点按被折叠成最后一次 —— 与服务端物理（移动本来就是每 tick 一步）同粒度。
    /// </summary>
    [Fact]
    public void SubTickPressesCollapseToTheLastOnePerTick()
    {
        var s = New();
        s.SetIntent(1, Epoch, A(1), WallOf(Tick0));
        s.AdvanceTo(Tick0 + 1);
        Assert.Equal(1, s.OpHistoryCount);
        Assert.True(s.TryGetOpState(1, out var first));
        Assert.Equal(0.5f, first.X, 4);

        // 同一个 tick 内按三次：+X、-X、+X
        s.SetIntent(2, Epoch, A(1), WallOf(Tick0 + 1));
        s.SetIntent(3, Epoch, A(-1), WallOf(Tick0 + 1));
        s.SetIntent(4, Epoch, A(1), WallOf(Tick0 + 1));
        s.AdvanceTo(Tick0 + 2);

        Assert.Equal(2, s.OpHistoryCount);              // ★ 每个 tick 恰好一条
        Assert.True(s.TryGetOpState(2, out var second));
        Assert.Equal(1.0f, second.X, 4);                // ★ 生效的是最后一次（+X），不是中间那次
    }

    /// <summary>
    /// 帧驱动（真实用法）：60fps 每帧只推进 1/3 tick，模拟仍然按**整 tick**走 ——
    /// 每个 tick 恰好一条操作、角色正常移动、序号与状态不错位。
    ///
    /// 改动前这里是坏的：整步几乎不发生 ⇒ 输入根本没被记录 ⇒ 角色一动不动（且有 30 条错位的环记录）。
    /// </summary>
    [Fact]
    public void FrameDrivenAdvanceSamplesExactlyOneOpPerTick()
    {
        var s = New();
        s.SetIntent(1, Epoch, A(1), WallOf(Tick0));

        // 31 帧 × 1/3 tick = 10.33 tick（最后一帧故意留余量）
        for (var i = 1; i <= 31; i++) s.AdvanceTo(Tick0 + i / 3.0);

        Assert.Equal(10, s.OpHistoryCount);        // ★ 每 tick 恰好一条（不是每帧一条）
        Assert.Equal(Tick0 + 10, s.StateTick);     // 模拟停在整 tick 上
        Assert.Equal(5.0f, s.State.X, 4);          // ★ 角色真的动了（10 步 × 0.5）
        Assert.True(s.TryGetOpState(10, out var last));
        Assert.Equal(5.0f, last.X, 4);

        // 帧余量只体现在渲染位置（在"上一条 tick → 最新 tick"之间插值），模拟状态不被污染
        var rendered = s.RenderedState.X;
        Assert.True(rendered >= s.State.X - 0.5f - 1e-4f && rendered <= s.State.X + 1e-4f,
            $"帧余量插值应有界（S(n-1)..S(n)）：rendered={rendered:F3} state={s.State.X:F3}");

        // 下一步（跨过整 tick）之后，模拟继续按整 tick 前进
        s.AdvanceTo(Tick0 + 11.0);
        Assert.Equal(11, s.OpHistoryCount);
        Assert.Equal(5.5f, s.State.X, 4);
    }

    /// <summary>
    /// 上行：每 tick 收集"未确认的操作"整批上传（冗余），条数有上限；
    /// ACK 之后窗口自动前移（已确认的不再重发）。
    /// </summary>
    [Fact]
    public void UnackedOpsAreCollectedForRedundantUpload()
    {
        var s = New();
        var batch = new List<ClientSmoother<FakeState, FakeAction>.OpRef>();
        s.SetIntent(1, Epoch, A(1), WallOf(Tick0));

        s.AdvanceTo(Tick0 + 5);
        // 批量 = 头部 1 条（最旧未确认 = 唯一能卡住服务端的那条）+ 最新 2 条
        Assert.Equal(3, s.CollectUnackedOps(batch));
        Assert.Equal(1UL, batch[0].Seq);               // 1 = 头部（补缺口）
        Assert.Equal(4UL, batch[1].Seq);               // 4、5 = 最新两条（保流不停 + 丢包覆盖）
        Assert.Equal(5UL, batch[^1].Seq);
        Assert.Equal(1, batch[0].Action.Dx);

        // 服务端确认到第 3 条 → 头部窗口前移
        s.OnSnapshot(Snap(Tick0 + 5, s.State.X, 3), WallOf(Tick0 + 5));
        s.AdvanceTo(Tick0 + 6);
        Assert.Equal(3, s.CollectUnackedOps(batch));
        Assert.Equal(4UL, batch[0].Seq);
        Assert.Equal(6UL, batch[^1].Seq);
    }

    /// <summary>
    /// ★ 冗余窗口**必须从最旧的未确认那条开始**：它就是服务端在等的缺口。
    ///
    /// 回归保护（真端到端踩出来的）：这里曾经还取了一个"最近 max 条"的下界
    /// （`windowStart = _opSeq - max + 1`），未确认数超过 max 时最旧那条就**永远不再重发**
    /// ⇒ 服务端等满宽限只能跳缺口 ⇒ **永久少走一条操作的位移**（0.5 格 = 一个 tick），
    /// 且 ACK 照常前进 ⇒ 客户端此后每份快照都要校正一次、偶尔直接贴。
    /// 端到端把"缺口跳过"关掉做对照后，干净链路的误差峰值 1.5~2.4 → 0.92、直接贴 3~9 → 0。
    /// </summary>
    [Fact]
    public void RedundantWindowAlwaysCarriesTheOldestUnackedOp()
    {
        var s = New();
        var batch = new List<ClientSmoother<FakeState, FakeAction>.OpRef>();
        s.SetIntent(1, Epoch, A(1), WallOf(Tick0));

        s.AdvanceTo(Tick0 + 10);                       // 采样出 10 条，一条都没被确认
        Assert.Equal(10, s.OpHistoryCount);

        // 上限 3 ⇒ 头部 1 条（1 = 服务端唯一可能在等的那条）+ 最新 2 条（9、10）。
        // 关键是**第 1 条必须在**：它不来，服务端只能跳缺口 ⇒ 永久少走一条操作的位移。
        Assert.Equal(3, s.CollectUnackedOps(batch, max: 3));
        Assert.Equal(1UL, batch[0].Seq);
        Assert.Equal(9UL, batch[1].Seq);
        Assert.Equal(10UL, batch[^1].Seq);

        // 再发一轮：缺口没被确认，同一条继续重发（同时仍然带着最新两条）
        Assert.Equal(3, s.CollectUnackedOps(batch, max: 3));
        Assert.Equal(1UL, batch[0].Seq);
        Assert.Equal(10UL, batch[^1].Seq);

        // 服务端跳过缺口（确认到 3）之后，头部窗口自动前移
        s.OnSnapshot(Snap(Tick0 + 10, s.State.X, 3), WallOf(Tick0 + 10));
        Assert.Equal(3, s.CollectUnackedOps(batch, max: 3));
        Assert.Equal(4UL, batch[0].Seq);
    }

    /// <summary>
    /// ★ 渲染平滑度：**60fps 帧 × 20Hz tick 下，逐帧渲染位移必须基本均匀**。
    ///
    /// 这条测试是端上"走路一卡一卡"的**确定性复现**：真客户端逐帧实测（直线行走 599 帧）
    /// 逐帧位移在 0.053~0.168 之间乱跳（≈3~10 格/秒），还有 5% 的帧**倒退**（最深一整步）。
    /// 位置不平滑有两个后果：① 画面本身一顿一顿；② <c>LocomotionPresentation</c> 是拿
    /// **这一帧的位移**算速度和"走/停"的 ⇒ 走路片段忽快忽慢、偶尔被切成 idle 再重播。
    ///
    /// 判据：每 tick 3 帧（每帧 1/3 tick），看每帧渲染位移的 max/min 比值（越接近 1 越平滑）。
    /// </summary>
    [Fact]
    public void RenderedMotionIsUniformAtSixtyFps()
    {
        var s = New();
        s.SetIntent(1, Epoch, A(1), WallOf(Tick0));
        // 先跑几个 tick 让链头和余量进入稳态
        for (var f = 0; f < 9; f++) s.AdvanceFrame(WallOf(Tick0 + f / 3.0 + 0.0001));

        var prev = s.RenderedState.X;
        var steps = new List<float>();
        for (var f = 9; f < 120; f++)
        {
            s.AdvanceFrame(WallOf(Tick0 + f / 3.0 + 0.0001));
            var now = s.RenderedState.X;
            steps.Add(now - prev);
            prev = now;
        }

        var min = steps.Min();
        var max = steps.Max();
        Assert.True(min > 0f, "渲染位置不得倒退");
        Assert.True(max / Math.Max(min, 1e-6f) < 1.2f,
            $"逐帧位移不匀：min={min:F4} max={max:F4} 比值={max / Math.Max(min, 1e-6f):F2}（应 <1.2）");
    }

    /// <summary>
    /// ★ 默认渲染 = **在"上一条 tick 状态"和"最新 tick 状态"之间线性插值**（帧余量就是插值系数）。
    ///
    /// 这样两个端点都是**已知的**：逐帧位移严格线性、不会"猜错了再回抽"。
    /// 端上逐帧实测（真客户端直线行走 599 帧）旧的外推方案：30 帧 ΔX&lt;0（最深倒退一整步）、
    /// 逐帧位移 0.053~0.168 乱跳（≈3~10 格/秒），而其中有些帧根本没校正 ⇒ 问题在渲染合成。
    /// </summary>
    [Fact]
    public void RenderedPositionInterpolatesBetweenTheLastTwoTickStates()
    {
        var s = New();
        s.SetIntent(1, Epoch, A(1), WallOf(Tick0));

        s.AdvanceTo(Tick0 + 1.0);
        var s1 = s.State.X;                            // S(n-1)
        s.AdvanceTo(Tick0 + 2.0);
        var s2 = s.State.X;                            // S(n)
        Assert.True(s2 > s1);

        // 帧余量 0（正好落在整 tick）：渲染应当停在**上一条** tick 状态（α=0），不是最新状态
        Assert.Equal(s1, s.RenderedState.X, 4);
        // 帧余量 0.5：正好是中点
        s.AdvanceTo(Tick0 + 2.5);
        Assert.Equal((s1 + s2) / 2f, s.RenderedState.X, 4);
        // 帧余量 → 1：走到最新状态
        s.AdvanceTo(Tick0 + 2.999);
        Assert.Equal(s2, s.RenderedState.X, 2);
        // 全程不得越过最新状态（插值，不是外推）
        Assert.True(s.RenderedState.X <= s2 + 1e-3f);
    }

    /// <summary>
    /// 外推模式（<see cref="NetcodeConfig.OwnRenderDelayTicks"/>=0）仍然可用：零额外延迟，
    /// 但渲染会**越过**最新状态半个 tick（那就是"猜"的部分，猜错就回抽）。
    /// </summary>
    [Fact]
    public void ExtrapolatingRenderModeIsStillAvailable()
    {
        var s = New(new NetcodeConfig { ClockMaxStepMs = 0, TickIndexedIntents = true, OwnRenderDelayTicks = 0 });
        s.SetIntent(1, Epoch, A(1), WallOf(Tick0));
        s.AdvanceTo(Tick0 + 2.5);

        Assert.True(s.RenderedState.X > s.State.X + 0.01f,
            $"外推模式应当越过最新状态: rend={s.RenderedState.X} state={s.State.X}");

        var i = New();                                  // 默认 = 插值：应当在最新状态**之后**（滞后）
        i.SetIntent(1, Epoch, A(1), WallOf(Tick0));
        i.AdvanceTo(Tick0 + 2.5);
        Assert.True(i.RenderedState.X < i.State.X - 0.01f,
            $"插值模式应当落后最新状态: rend={i.RenderedState.X} state={i.State.X}");
    }

    /// <summary>
    /// ★ 一次校正**不能让渲染位置跳**：校正该由残差平滑慢慢消化，而不是让画面"啪"地贴过去。
    ///
    /// 这条是端上逐帧追出来的：一开始我在 rebase 时把插值端点作废（怕它按校正前的位移画），
    /// 结果那一帧渲染直接贴到最新模拟状态（比插值点靠前将近一个 tick），下一帧再回来 ——
    /// 校正频繁时（走动中约 43% 的帧带校正）表现成 `offsetDist` 在 0.03↔0.34 之间跳、
    /// 逐帧位移在 ±0.2 之间来回，就是"走路一卡一卡"。
    /// 正确做法：插值模式下端点留着（插值被夹在 [S(n-1), S(n)] 内，不会跑飞），
    /// 校正量交给 <see cref="ClientSmoother{TState,TAction}.Residual"/> 去平滑。
    /// </summary>
    [Fact]
    public void CorrectionDoesNotJumpTheRenderedPosition()
    {
        var s = New();
        s.SetIntent(1, Epoch, A(1), WallOf(Tick0));
        s.AdvanceTo(Tick0 + 2.5);                     // 有余量 ⇒ 正在插值
        Assert.True(s.RenderOffsetDistance > 0.01f, $"应当有 tick 间插值偏移: {s.RenderOffsetDistance}");
        var before = s.RenderedState.X;

        // 服务端说"执行完第 2 条之后你在别处"（超过死区 ⇒ rebase + 重放）
        var report = s.OnSnapshot(Snap(Tick0 + 2, s.State.X + 1.0f, 2), WallOf(Tick0 + 3));
        Assert.NotEqual(CorrectionKind.Deadzone, report.Kind);

        var after = s.RenderedState.X;
        Assert.True(Math.Abs(after - before) < 0.05f,
            $"校正瞬间渲染位置跳了：{before:F4} → {after:F4}（应交给残差平滑慢慢收）");
        Assert.True(s.Blending, "校正应当进入残差平滑");
    }

    /// <summary>
    /// 离散操作（攻击/合成/取消…）与移动**共用一条输入流**：它们也要占一个序号（服务端按序号
    /// 连续消费，缺号就会一直等），但**不占移动步**、也不走移动的冗余重发通道。
    /// </summary>
    [Fact]
    public void DiscreteOpsShareTheSameSequenceButDoNotStep()
    {
        var s = New();
        var batch = new List<ClientSmoother<FakeState, FakeAction>.OpRef>();
        s.SetIntent(1, Epoch, A(1), WallOf(Tick0));
        s.AdvanceTo(Tick0 + 2);                       // 两条移动操作：1、2
        Assert.Equal(2, s.OpHistoryCount);

        var seq = s.ReserveDiscreteOp(WallOf(Tick0 + 2));   // 第 3 条 = 离散操作（攻击）
        Assert.Equal(3UL, seq);
        Assert.True(s.IsDiscreteOp(3));
        Assert.False(s.IsDiscreteOp(2));

        s.AdvanceTo(Tick0 + 3);                       // 再一条移动操作：4
        s.SetIntent(1, Epoch, A(1), WallOf(Tick0 + 3));
        s.AdvanceTo(Tick0 + 4);                       // 5

        // 冗余窗口只带移动操作（离散那条由协议层自己发）
        s.CollectUnackedOps(batch, max: 5);
        Assert.DoesNotContain(batch, op => op.Seq == 3UL);
        Assert.Contains(batch, op => op.Seq == 5UL);

        // ★ 同序号比较：离散操作那条"不占步"，权威状态 = 当时的位置 ⇒ 误差为 0
        Assert.True(s.TryGetOpState(3, out var atDiscrete));
        var report = s.OnSnapshot(Snap(Tick0 + 3, atDiscrete.X, 3), WallOf(Tick0 + 4));
        Assert.Equal(0f, report.Err, 5);
        Assert.Equal(CorrectionKind.Deadzone, report.Kind);
    }

    /// <summary>
    /// 【回归】新模式下"ack 的序号不在环里"（首份之后的超窗口/换代）**只能贴位置、不能动时间轴**。
    ///
    /// 否则那一次和解会把这一帧的时间"走掉"（旧路径的重放），活推进永远凑不满一个整 tick ⇒
    /// **一条操作都采不出来**（E2E 实测：链头 tick 跟着服务端涨了 142 个 tick，ops 恒为 0，客户端不动）。
    /// 另一面：首份快照**必须**把时间轴对齐到权威 tick，否则活推进会在下一帧把"从 0 到服务端现在"
    /// 整段跨完（实测一帧采出 4705 条操作、位置飞出地图）。
    /// </summary>
    [Fact]
    public void UnknownAckFallsBackToPositionOnlyAndKeepsSampling()
    {
        // ① 首份快照：时间轴对齐到权威 tick（而不是从 0 开始）——
        //    否则活推进会在下一帧把"从 0 到服务端现在"整段跨完（实测一帧采出 4705 条操作、位置飞出地图）。
        long bigTick = 5100;
        var fresh = new ClientSmoother<FakeState, FakeAction>(
            new FakeModel { Speed = Speed }, Indexed);
        fresh.SetIntent(1, Epoch, A(1), WallOf(bigTick));
        fresh.OnSnapshot(Snap(bigTick, 0f, 0), WallOf(bigTick));
        Assert.Equal(bigTick, fresh.StateTick);
        fresh.AdvanceTo(bigTick + 1);
        Assert.Equal(1, fresh.OpHistoryCount);        // ★ 只采一条，不是几千条

        // ② ack 指向环里没有的序号（超出回看窗口）⇒ 只贴位置，时间轴不动、采样继续
        var s = New();
        s.SetIntent(1, Epoch, A(1), WallOf(Tick0));
        for (var i = 1; i <= 5; i++) s.AdvanceTo(Tick0 + i);
        Assert.Equal(5, s.OpHistoryCount);

        var before = s.State.X;
        var report = s.OnSnapshot(Snap(Tick0 + 5, before + 0.4f, appliedSeq: 999), WallOf(Tick0 + 5));
        Assert.Equal(Tick0 + 5, s.StateTick);         // ★ 时间轴没被推走
        Assert.Equal(before + 0.4f, s.State.X, 4);    // ★ 位置贴上了权威
        Assert.True(report.Err > 0f);

        s.AdvanceTo(Tick0 + 6);
        Assert.Equal(6, s.OpHistoryCount);            // ★ 下一个 tick 照常采样
    }
}
