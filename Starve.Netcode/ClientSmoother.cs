namespace Starve.Netcode;

/// <summary>
/// 一条权威快照里与本端有关的部分（接入层解析协议后组装成这个中立结构）。
///
/// <paramref name="AppliedSeq"/> 是<b>服务端"已经烘进这份位置"到第几号输入</b>，
/// 不是"已经收到"——重放的起点由它决定，缺了它误差就不可能归零。
/// </summary>
public readonly record struct NetSnapshot<TState>(
    long Tick,
    ulong AppliedSeq,
    ulong Epoch,
    TState OwnState,
    bool OwnStopped)
    where TState : struct;

/// <summary>
/// 客户端预测 / 服务端权威校验和解 / 重放 —— 组件本体。
///
/// 三份缓存：
/// <list type="bullet">
/// <item><c>netCache</c>（<see cref="SnapshotWindow{TState}"/>）：权威快照，按 tick 有序；</item>
/// <item><c>localCache</c>（<see cref="InputHistory{TAction}"/>）：本端操作 + 生效 tick；</item>
/// <item><c>otherCache</c>（<see cref="RemoteEntity{TState}"/>）：别人的插值。</item>
/// </list>
///
/// 本端只有一条推进路径：<c>AdvanceTo</c>。正常推进和重放是同一句调用，
/// 所以不存在"两套推进结果不一致"。重放<b>只在本组件内部跑纯运动学</b>：
/// 不碰渲染、动画、音效、玩法，只产出"校正后的最新状态"。
///
/// 链头用 <see cref="StateVersion"/>（单调递增）标识，**绝不用 tick**——
/// 因为重放会让链头的 tick 往回退。
///
/// <b>模拟状态与渲染位置是两个值</b>：
/// <list type="bullet">
/// <item><see cref="State"/> = 重放结果（权威正确），后续预测的基准，**绝不带残差**；</item>
/// <item><see cref="RenderedState"/> = 残差平滑后的位置，只有相机/角色读它。</item>
/// </list>
/// 残差用"从**当前屏幕位置**混合到实时权威轨迹"的方式收敛（权重 1→0）：
/// w=1 时恰好等于原屏幕位置（接缝零跳变，连续校正不叠跳），
/// w=0 时精确落在轨迹上（按定义必然收敛，不过冲、不停在错位置）。
/// 残差**永不回流**进模拟，否则误差判据与下一次重放的基准都会被污染。
/// </summary>
public sealed class ClientSmoother<TState, TAction>
    where TState : struct
    where TAction : struct
{
    private readonly INetModel<TState, TAction> _model;
    private readonly IStateSpace<TState> _space;
    private readonly NetcodeConfig _config;
    private readonly SnapshotWindow<TState> _netCache;
    private readonly Dictionary<ulong, RemoteEntity<TState>> _remotes = new();

    private TState _state;
    /// <summary>
    /// <see cref="_state"/> 在**服务端时间轴**上的位置（含小数）。
    /// 必须带小数：活推进是逐帧的（渲染要连续），而重放是逐 tick 的（要复现服务端）。
    /// 若重放只走到整 tick，每次和解都会比活推进少走一个 tick 的小数部分 ——
    /// 表现就是"每份快照差半格"，实测正是如此。
    /// </summary>
    private double _stateTick;
    private uint _version;
    private bool _hasState;
    private ulong _epoch;
    private long _lastSnapshotTick;
    private bool _lastReplayHadMismatch;

    // 两次快照之间"纯预测"的累计（不含重放/校正），用于与权威做同跨度速度对比。
    private float _predAccum;
    private double _predSeconds;
    /// <summary>连续多少个 tick 没有实际位移（走动/静止判定用）。</summary>
    private int _ticksSinceMotion;
    /// <summary>最近一步的位移（诊断用）。</summary>
    private float _lastStepDistance;
    private int _predSteps;

    // 渲染层残差平滑：_rendered = _state + k × _residual，k 从 1 衰减到 0。
    // _residual = (校正那一刻的屏幕位置 - 当时的模拟位置)，是个**位移**，不随模拟前进变化。
    // 用"位移按比例衰减"而不是"在两点之间插值"：后者在模拟越过起点时会停顿/倒退。
    private TState _rendered;
    private bool _hasRendered;

    // 诊断：上一份快照的 (ack, 权威位置)，用来做"同跨度位移"对比（见 CorrectionReport.ClientMove）
    private ulong _diagAck;
    private TState _diagState;
    private bool _hasDiag;
    // 单程延迟估计（tick）：输入"什么时候在服务端生效"必须加上它，否则重放会把新意图
    // 提前应用 —— 每次转向都产生一次假的校正。用快照 ack 反推，自标定。
    private double _oneWayTicks;
    /// <summary>
    /// 输入的"生效延迟"（tick）= 往返。它与 <see cref="_oneWayTicks"/>（单程）**不是同一个量**：
    /// <list type="bullet">
    /// <item>预测目标要加**单程**：时钟的 tick 本身就落后服务端一个单程；</item>
    /// <item>输入的生效 tick 要加**往返**：从"我发出"到"服务端用上"要一个单程，
    ///   而记录的基准 tick 又落后服务端一个单程，两者叠加正好一个往返。</item>
    /// </list>
    /// 混成一个的后果：重放会把新方向提前一个单程生效，**每次转向都产生一次假失配**
    /// （实测 300ms 下误差均值 0.9 格、28% 的快照被判成"直接贴"）。
    /// </summary>
    private double _inputDelayTicks;
    private bool _hasOneWay;
    private ulong _lastCalibratedSeq;
    /// <summary>上一次喂时钟的墙钟（给"按时间"的限速预算用；long.MinValue = 还没喂过）。</summary>
    private long _lastClockMs = long.MinValue;

    // ── 【序号锚定和解】我自己的操作流：每个 tick 一条编号操作 + 执行完它之后我自己的状态 ──
    // 键是"我自己的操作序号"（= 我自己的 tick 编号），不是服务端的 tick。
    private readonly List<OpRecord> _opRing = new();
    private TAction _currentIntent;
    private bool _hasCurrentIntent;
    private ulong _currentOpIndex;
    /// <summary>【序号锚定模式】操作序号计数器：**就是一个自增 uint64**，与 tick 无关（只用于去重/排序/ACK）。</summary>
    private ulong _opSeq;
    /// <summary>【序号锚定模式】这一帧还没被模拟消费的 tick 余量（0~1）；只影响表现层，不进模拟。</summary>
    private double _renderRemainder;
    /// <summary>上一次整 tick 步进之前的模拟状态（用来把余量外推成渲染位移）。</summary>
    private TState _lastStepFrom;
    private bool _hasLastStep;

    /// <summary>一条已执行的操作：序号 / 它在我的时间轴上的 tick / 执行完之后的状态 / 动作。</summary>
    private readonly record struct OpRecord(
        ulong Index, long Tick, TState StateAfter, TAction Action, bool Discrete = false,
        /// <summary>执行这条操作那一步**实际走了多少格**（客户端自己的每 op 位移）。</summary>
        float StepDistance = 0f);

    private INetcodeMetrics? _metrics;
    private TState _residual;
    /// <summary>帧余量外推出的渲染位移（只加在 <see cref="RenderedState"/> 上，绝不进模拟）。</summary>
    private TState _renderLead;
    private double _blendElapsedTicks;
    private double _blendTotalTicks;
    private float _blendWeight;

    public ClientSmoother(INetModel<TState, TAction> model, NetcodeConfig? config = null, int snapshotCapacity = 64)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _space = model;
        _config = config ?? new NetcodeConfig();
        _netCache = new SnapshotWindow<TState>(snapshotCapacity);
        Clock = new ServerClock(_config);
        Inputs = new InputHistory<TAction>();
    }

    /// <summary>墙钟 ↔ 服务端 tick。</summary>
    public ServerClock Clock { get; }

    /// <summary>本端输入历史。</summary>
    public InputHistory<TAction> Inputs { get; }

    /// <summary>模拟状态：重放结果，权威正确，**不带任何残差**。玩法判定用它。</summary>
    public TState State => _state;

    /// <summary>
    /// 渲染位置：残差平滑 + tick 间插值/外推（纯表现层）。相机与角色节点读这个（不是 <see cref="State"/>）。
    ///
    /// 默认按 <see cref="NetcodeConfig.OwnRenderDelayTicks"/>=1 在**上一条与最新 tick 状态之间插值**，
    /// 所以它比 <see cref="State"/> 晚约一个 tick（视觉更顺，代价是 50ms 显示延迟）。
    /// </summary>
    public TState RenderedState =>
        _hasRendered ? _space.AddDelta(_rendered, _renderLead, 1f) : _state;

    /// <summary>平滑权重（1 → 0）；0 = 已经与模拟状态重合。</summary>
    public float CorrectionBlendWeight => _blendWeight;

    /// <summary>
    /// **客户端最近一次整 tick 步进的实际位移**（诊断用，和服务端的"每 op 位移"比）。
    ///
    /// 为什么它不受校正污染：rebase 时我把端点跟着校正量一起平移（见
    /// <see cref="RebaseRenderEndpoints"/>），所以 Δ = S(n)−S(n−1) 始终是"我这一步走了多少"。
    /// 用它和服务端 `smv/dops` 对比即可判失配性质：
    ///   · 两者相等而 err 大 ⇒ **同速不同向**（贴障碍滑动/动态避让的模型差异）；
    ///   · 两者不等 ⇒ **速度差**（坡度/有效速度/少走一步）。
    /// </summary>
    public float LastStepDistance =>
        _hasLastStep ? _space.Distance(_lastStepFrom, _state) : 0f;

    /// <summary>
    /// 当前 tick 间插值偏移的位移量（诊断用）：0 = 本帧直接画最新模拟状态。
    ///
    /// 什么时候是 0：还没走过整 tick（<c>_hasLastStep=false</c>）、
    /// 或者**刚被一次校正 rebase 过**（旧的插值端点已失效，故意不插值 —— 否则会按校正前的位移画，
    /// 渲染位置跳一下/回抽）。默认（插值）模式下它等于"落后最新状态多少格"。
    /// 端上排查"走路一卡一卡"时，这一项和 <see cref="Residual"/>、<see cref="Blending"/> 一起看。
    /// </summary>
    public float RenderOffsetDistance => _space.Distance(default, _renderLead);

    /// <summary>是否正在做残差平滑过渡。</summary>
    public bool Blending => _blendTotalTicks > 0;

    /// <summary>
    /// 当前待消化的残差（位移）：<c>RenderedState − State</c>。
    ///
    /// 给"想自己做表现层平滑"的业务用的**逃生口**：自己按 <c>State + k × Residual</c>
    /// 算渲染位置即可（k 由你决定），也可以直接接 <see cref="RenderedState"/> 用默认实现。
    ///
    /// ⚠️ 无论谁做，**同一份残差只能被积分一次** —— 组件和业务都去消化它，等于两次积分，
    /// 位置必然漂移（那就是"两个真相源"）。
    /// </summary>
    public TState Residual => _residual;

    /// <summary>残差平滑还剩多少 tick 收完（0 = 未在过渡）。</summary>
    public int BlendTicksRemaining =>
        _blendTotalTicks <= 0
            ? 0
            : (int)Math.Ceiling(Math.Max(0.0, _blendTotalTicks - _blendElapsedTicks));

    /// <summary>本次过渡总时长（tick）；0 = 未在过渡。</summary>
    public int BlendTicksTotal => _blendTotalTicks <= 0 ? 0 : (int)Math.Ceiling(_blendTotalTicks);

    /// <summary>当前链头版本（单调递增）。</summary>
    public uint StateVersion => _version;

    /// <summary>当前链头对应的服务端 tick（整 tick，向下取整）。</summary>
    public long StateTick => (long)Math.Floor(_stateTick);

    /// <summary>是否已经建立过链（第一份权威快照后为 true）。</summary>
    public bool HasState => _hasState;

    /// <summary>预测器回传过期版本的累计次数（&gt;0 说明预测模块有问题，应该报警）。</summary>
    public int VersionMismatchCount { get; private set; }

    /// <summary>最近一次和解的诊断。</summary>
    public CorrectionReport LastReport { get; private set; }

    /// <summary>
    /// 纠偏通知：**只在真的发生了校正时**触发（死区不触发），且只给"最终值"——
    /// 回调里读 <see cref="State"/> 就是校正后的最新状态，不需要（也不应该）重放中间帧。
    /// </summary>
    public Action<CorrectionReport>? OnCorrected { get; set; }

    public IReadOnlyDictionary<ulong, RemoteEntity<TState>> Remotes => _remotes;

    /// <summary>
    /// 指标出口：真实业务实现 <see cref="INetcodeMetrics"/>，把数据接到自己的日志上。
    /// 未设置（null）时组件不做任何额外工作。
    /// </summary>
    public INetcodeMetrics? Metrics
    {
        get => _metrics;
        set => _metrics = value;
    }

    /// <summary>估计的单程延迟（tick）；用于把输入映射到"服务端在哪个 tick 生效"。</summary>
    public double EstimatedOneWayTicks => _oneWayTicks;

    /// <summary>估计的"输入生效延迟"（tick）≈ 往返。</summary>
    public double EstimatedInputDelayTicks => _inputDelayTicks;

    /// <summary>
    /// **服务端"现在"大概在哪个 tick** = <c>Clock.TickAt(now) + 单程延迟</c>。
    ///
    /// ⚠️ 这个加项是必须的：<see cref="Clock"/> 的 tick 是"我此刻最该收到的那份快照对应哪一刻"，
    /// 它天然**落后服务端一个单程延迟**（对别人做插值正好用它，那是我们要的"渲染在过去"）。
    /// 但自己的预测必须推到服务端的**当前**时刻，否则本地玩家会整整慢一个延迟，
    /// 而且每份快照都会产生一次假的校正。
    ///
    /// 用法：自己的 <see cref="AdvanceFrame"/> 内部已经加了；
    /// 远端实体插值也用这个值（再减 <c>InterpolationDelayTicks</c>）。
    /// </summary>
    public double ServerNowTick(long nowMs) => Clock.TickAt(nowMs) + _oneWayTicks;

    public NetcodeConfig Config => _config;

    /// <summary>
    /// 本端输入：设置**当前意图**。
    ///
    /// 序号锚定模式下它只是"当前意图"的入口 —— 真正的操作由推进时**按固定间隔采样**产生
    /// （见 AdvanceTo），所以长按不是"发一条然后靠服务端一直沿用"，而是每秒采样 20 条：
    /// 每条 = 一个 tick 的意图，客户端缓存起来，未确认的会冗余上传，服务端去重排序后按序消费。
    /// 旧路径下才是"变化时记一条"。
    ///
    /// 记录两个 tick：<c>baseTick</c> = 发送时刻的服务端 tick（用于标定延迟）；
    /// <c>effectiveTick</c> = baseTick + 单程延迟估计 = **服务端大概在哪个 tick 用上它**。
    /// 重放按 effectiveTick 取意图；少了这一项，重放会把新意图提前生效，
    /// 每次转向都会产生一次假的失配。
    /// </summary>
    public void SetIntent(ulong seq, ulong epoch, in TAction action, long nowMs)
    {
        // ⚠️ 必须在这里也认下会话代号：输入常常比第一份快照先到，
        // 若只在快照侧更新代号，第一份快照会被判成"换会话"，把刚记下的输入清空
        // —— 表现是"客户端一直不动"（实测踩过）。
        if (epoch != _epoch)
        {
            if (_epoch != 0) ResetSession(epoch);
            else _epoch = epoch;
        }

        // 时钟还没同步时 TickAt 返回 0，拿它当基准会算出天文数字的延迟
        // （实测把单程估计顶到上限 40 tick，客户端直接飞出去）。
        // 这类记录标记为 NaN：不参与标定，但 effectiveTick 取 0 保证"任何 tick 都生效"。
        var synced = Clock.HasSync;
        var baseTick = synced ? Clock.TickAt(nowMs) : double.NaN;
        var effectiveTick = synced ? baseTick + _inputDelayTicks : 0.0;

        // 【序号锚定模式】这里**不**记历史：真正的操作在推进时按 tick 采样（序号 = 我自己的 tick 编号），
        // 这样"我这条操作从哪一 tick 起生效"是客观事实，不用估任何在途时间。
        if (!_config.TickIndexedIntents)
        {
            Inputs.Record(seq, epoch, baseTick, effectiveTick, action);
        }
        else
        {
            _currentIntent = action;
            _hasCurrentIntent = true;
        }
    }

    /// <summary>【序号锚定模式】对外暴露"当前操作序号"：网络层要把它当作 seq 发出去（服务端原样回执）。</summary>
    public ulong CurrentOpIndex => _currentOpIndex;

    /// <summary>是否启用序号锚定和解（= 每个 tick 一条编号操作）。</summary>
    public bool TickIndexedIntents => _config.TickIndexedIntents;

    /// <summary>【序号锚定模式】环里存了多少条"操作之后的状态"（诊断/测试用）。</summary>
    public int OpHistoryCount => _opRing.Count;

    /// <summary>是否已经有"当前意图"（新模式下没有它就不会采样操作）。</summary>
    public bool HasIntent => _hasCurrentIntent;

    /// <summary>服务端**已消费**到的操作序号（来自快照 ACK）。0 = 还没有。</summary>
    public ulong LastAckedOpIndex { get; private set; }

    /// <summary>一条待上传的操作（序号 + 动作）。</summary>
    public readonly record struct OpRef(ulong Seq, TAction Action);

    /// <summary>
    /// 【序号锚定模式·上行】收集"服务端还没确认的操作"，最多 <paramref name="max"/> 条。
    ///
    /// 网络层每个 tick 把这一整批发出去（**冗余上传**：未确认的操作会被反复携带）——
    /// 丢包不会丢输入，服务端按 seq 去重排序即可（见服务端 TestRedundantAndOutOfOrderOpsAreDeduped）。
    /// </summary>
    public int CollectUnackedOps(List<OpRef> into, int max = 3)
    {
        into.Clear();
        if (!_config.TickIndexedIntents || _opSeq == 0) return 0;
        if (max < 1) max = 1;

        // 冗余批量 = **从最旧未确认开始的连续窗口**（补缺口）+ **最新那条**（保流不停）。
        //
        // ⚠️ 两个方向都踩过坑，必须同时满足：
        //   · 只发"最近 max 条"（早先实现：`windowStart = _opSeq - max + 1`）：未确认数超过 max 时，
        //     最旧那条**永远不再重发**。而服务端是**按序号连续**消费的 ⇒ 唯一能卡住它的就是
        //     `ack+1` 那条；它不来，服务端等满宽限只能跳缺口 ⇒ **永久少走一条操作的位移**
        //     （0.5 格 = 一个 tick），ACK 却照常前进 ⇒ 客户端此后每份快照都要校正一次、
        //     偶尔越过阈值直接贴。这是**丢一条操作就永久偏一格**的确定性缺陷，
        //     与"要不要等缺口"无关：客户端每帧都必须把 `ack+1` 带上，服务端才有机会补齐。
        //   · 只发"最旧那条 + 最新"（第一版修法）：高延迟下 ack 滞后很多，名额会被早已确认的
        //     那几条吃掉，**新操作发不出去** ⇒ 服务端队列见底、流停住，客户端预测直接跑飞
        //     （仿真 500ms 延迟档误差峰值 27）。
        //
        // 为什么头部窗口就够：服务端**连续消费**，所以它唯一等的就是 `ack+1`；比它更靠后的空洞
        // 不可能卡住它（那些操作晚到一点也会被顺序消费）。于是：头部窗口补"能卡住它的那条"，
        // 最后一个名额留给"最新那条"保推进。
        var first = LastAckedOpIndex + 1;
        if (first > _opSeq) return 0;                       // 全部确认完了
        // ① 头部固定占 1 个名额：`ack+1` —— 服务端连续消费，唯一能卡住它的就是这条。
        if (!IsDiscreteOp(first) && TryGetOpAction(first, out var head))
        {
            into.Add(new OpRef(first, head));
        }
        // ② 其余名额给**最新的 max-1 条**：保证"新操作"发得出去（否则高延迟下流会停），
        //    同时让每条操作在它还是"最新几条"时被重发若干次 —— 这是丢包覆盖的来源。
        var newest = max - 1;
        var recentStart = _opSeq > (ulong)newest ? _opSeq - (ulong)newest + 1 : 1UL;
        for (var seq = recentStart; seq <= _opSeq && into.Count < max; seq++)
        {
            if (IsDiscreteOp(seq)) continue;   // 离散操作不走移动上行通道
            if (into.Count > 0 && into[^1].Seq >= seq) continue;   // 头部已经带过
            if (TryGetOpAction(seq, out var action)) into.Add(new OpRef(seq, action));
        }

        return into.Count;
    }

    /// <summary>
    /// 【序号锚定模式】为一条**离散操作**（攻击/合成/取消…）预留序号。
    ///
    /// 为什么必须共用一条流：服务端按"序号连续"消费（缺口就等补齐），移动和离散操作
    /// 如果各自编号，服务端看到的流就会缺号/重号 ⇒ 一直在等一个永远不来的缺口。
    ///
    /// 离散操作**不占移动步**：环里记的"应用完它之后的状态"就是当前状态（位置没变），
    /// 服务端那边同样不推进移动 ⇒ 两边同序号状态仍然相等。
    /// 它也不会出现在移动的冗余重发窗口里（那条通道只发移动操作）。
    /// </summary>
    public ulong ReserveDiscreteOp(long nowMs)
    {
        var seq = ++_opSeq;
        var tick = (long)Math.Floor(_stateTick + 1e-9);
        Inputs.Record(seq, _epoch, _stateTick, _stateTick, _currentIntent);
        _opRing.Add(new OpRecord(seq, tick, _state, _currentIntent, Discrete: true));
        if (_opRing.Count > _config.OpHistoryCapacity) _opRing.RemoveAt(0);
        return seq;
    }

    /// <summary>这条序号是不是"离散操作"（不占移动步、不由移动通道发送）。</summary>
    public bool IsDiscreteOp(ulong index)
    {
        for (var i = _opRing.Count - 1; i >= 0; i--)
        {
            if (_opRing[i].Index == index) return _opRing[i].Discrete;
        }

        return false;
    }

    /// <summary>取某条操作的动作（上行重发用）。</summary>
    public bool TryGetOpAction(ulong index, out TAction action)
    {
        for (var i = _opRing.Count - 1; i >= 0; i--)
        {
            if (_opRing[i].Index != index) continue;
            action = _opRing[i].Action;
            return true;
        }

        action = default;
        return false;
    }

    /// <summary>取"执行完第 <paramref name="index"/> 条操作之后，我自己的状态"（诊断/测试用）。</summary>
    public bool TryGetOpState(ulong index, out TState state)    {
        for (var i = _opRing.Count - 1; i >= 0; i--)
        {
            if (_opRing[i].Index != index) continue;
            state = _opRing[i].StateAfter;
            return true;
        }

        state = default;
        return false;
    }

    /// <summary>
    /// 强制把链贴到指定状态（出生/传送/复活用）。
    ///
    /// 为什么必须是显式 API：序号锚定和解靠"我执行完第 N 条之后的状态"和权威比，
    /// 一次传送会让**环里已有的那些状态整体作废**（它们属于传送前的地图位置）。
    /// 所以这里同时清掉操作环：之后的比较只从传送后的新状态开始，不会拿旧位置去对。
    /// </summary>
    public void ForceSnapTo(TState state, long nowMs)
    {
        _state = state;
        _stateTick = Math.Max(_stateTick, ServerNowTick(nowMs));
        _version++;
        _hasState = true;
        _opRing.Clear();
        _renderRemainder = 0;
        _renderLead = default;
        _hasLastStep = false;
        ClearBlend();
        _rendered = _state;
        _hasRendered = true;
    }

    /// <summary>换会话：清空全部历史与链，等下一份权威快照重建。</summary>
    private void ResetSession(ulong epoch)
    {
        _epoch = epoch;
        Inputs.Clear(epoch);
        _netCache.Clear(epoch);
        foreach (var remote in _remotes.Values) remote.Clear(epoch);
        _hasState = false;
        ClearBlend();
        _hasOneWay = false;
        _oneWayTicks = 0;
        _inputDelayTicks = 0;
        _lastCalibratedSeq = 0;
        _lastClockMs = long.MinValue;   // 换会话：时钟预算重新起算（第一份是硬同步）
        _opSeq = 0;
        LastAckedOpIndex = 0;
        _opRing.Clear();
        _renderRemainder = 0;
        _hasLastStep = false;
        _renderLead = default;
    }

    /// <summary>
    /// 每帧调用（**推荐入口**）：推进到"服务端现在"。
    /// 自己走预测必须用这个；用 <see cref="AdvanceTo"/> 会少推一个单程延迟。
    /// </summary>
    public void AdvanceFrame(long nowMs)
    {
        AdvanceTo(ServerNowTick(nowMs), replay: false);
        UpdateRendered();
    }

    /// <summary>
    /// 低层入口：推进到指定 tick（确定性测试 / 自定义时钟用）。
    /// 正常业务请用 <see cref="AdvanceFrame"/>。
    /// </summary>
    public void AdvanceTo(double targetTick)
    {
        AdvanceTo(targetTick, replay: false);
        UpdateRendered();
    }

    /// <summary>现在比最近一份快照新了多少 tick；超过 StallMs 判定为断线。</summary>
    public bool Starved(double nowTick) =>
        _hasState && nowTick - _lastSnapshotTick > _config.StallMs / _config.TickMs;

    /// <summary>取/建某个远端实体的插值器。</summary>
    public RemoteEntity<TState> Remote(ulong id)
    {
        if (!_remotes.TryGetValue(id, out var remote))
        {
            remote = new RemoteEntity<TState>(_model, _config);
            if (_epoch != 0) remote.Clear(_epoch);
            _remotes[id] = remote;
        }

        return remote;
    }

    public bool RemoveRemote(ulong id) => _remotes.Remove(id);

    public void ClearRemotes() => _remotes.Clear();

    /// <summary>最近一份权威快照（诊断：可与 <see cref="State"/> 对比看领先量）。</summary>
    public bool TryGetAuthoritative(out SnapshotEntry<TState> entry) => _netCache.TryLatest(out entry);

    public bool TryGetPreviousAuthoritative(out SnapshotEntry<TState> entry) =>
        _netCache.TryPrevious(out entry);

    /// <summary>
    /// 收到权威快照：<c>Rebase(权威) → AdvanceTo(now)</c>。后半句就是"重放"。
    /// </summary>
    public CorrectionReport OnSnapshot(in NetSnapshot<TState> snapshot, long nowMs)
    {
        var epochChanged = snapshot.Epoch != _epoch;
        if (epochChanged) ResetSession(snapshot.Epoch);

        var inserted = _netCache.Insert(
            snapshot.Tick, snapshot.OwnState, snapshot.AppliedSeq, snapshot.Epoch);

        // ⚠️ 标定必须在 Acknowledge 之前：它要用"这条输入是什么时候发的"这条记录，
        // 裁掉之后就查不到了（实测踩过：单程估计恒为 0 → 预测永远慢一个延迟）。
        CalibrateOneWay(snapshot.Tick, snapshot.AppliedSeq);

        // ack 无论新旧都要吃（服务端确实处理到了），但它不构成"可以回退链"的理由。
        // 保留最近若干条：重放窗口要用"当时那条意图"，而那几条通常已经被确认了。
        if (snapshot.AppliedSeq > LastAckedOpIndex) LastAckedOpIndex = snapshot.AppliedSeq;
        Inputs.Acknowledge(snapshot.AppliedSeq,
            keepLast: Math.Max(_config.MaxReplayTicks + 4,
                _config.TickIndexedIntents ? _config.OpHistoryCapacity : 0));
        if (snapshot.Tick > _lastSnapshotTick) _lastSnapshotTick = snapshot.Tick;

        // 只有带来新时间信息的快照才喂时钟：迟到包不该影响时间基准。
        if (inserted || epochChanged)
        {
            // ⚠️ 时钟限速必须按**经过的墙钟时间**给预算，不能"每份快照都吃满上限"：
            //    延迟恢复时一次帧里会补来一整个突发（实测 22 份），逐份 10ms 就是 220ms ——
            //    时间轴一帧突进 4.4 tick，屏幕跟不上 ⇒ 残差超上限 ⇒ 一次可见突跳（"直接贴"）。
            //    按时间给预算后，同一帧里只有第一份拿到与"距上次喂时钟的时长"成比例的额度，
            //    平均校正速率不变（10ms/50ms = 200ms/秒），突发则被真正限住。
            var elapsedTicks = _lastClockMs == long.MinValue
                ? 1.0
                : Math.Clamp((nowMs - _lastClockMs) / _config.TickMs, 0.0, 8.0);
            Clock.OnSnapshot(snapshot.Tick, nowMs, snapshot.Epoch, _config.ClockMaxStepMs * elapsedTicks);
            _lastClockMs = nowMs;
            if (Clock.HardResynced) _metrics?.OnClockResync(snapshot.Tick, nowMs, Clock.OffsetMs);
        }

        if (!inserted)
        {
            // 迟到包：不代表当前时刻的权威位置，绝不能拿它把已经预测到前面的链拉回去。
            LastReport = new CorrectionReport(
                CorrectionKind.Stale, snapshot.Tick, (long)Math.Floor(_stateTick), _version, snapshot.AppliedSeq,
                0f, 0, false, false, 0, 0f, 0f, Starved(Clock.TickAt(nowMs)),
                Blending ? (int)Math.Ceiling(_blendTotalTicks) : 0);
            _metrics?.OnCorrection(LastReport); // 迟到/乱序也要能被业务统计到
            return LastReport;
        }

        var hadState = _hasState;

        // ── 【序号锚定和解】先试新路径：比"同一个操作前缀之后的状态"，完全不碰服务端的钟 ──
        if (_config.TickIndexedIntents)
        {
            var byIndex = ReconcileByOpIndex(snapshot, nowMs, hadState);
            if (byIndex is { } indexed) return indexed;

            // ⚠️ 找不到那条操作（首份快照 / 延迟超出回看窗口 / 换会话）时**只贴位置、不动时间轴**。
            //
            // 为什么不能退回旧路径（重放）：旧路径会把链**推进到服务端的现在**，那一帧的"时间"
            // 就被重放（replay:true）消费掉了 —— 而新模式下操作是在**活推进**里采样的，
            // 于是时间轴被重放一次次吃掉、活推进永远凑不满一个整 tick ⇒ **一条操作都采不出来**
            // （实测：链头 tick 跟着服务端涨了 142 个 tick，ops 却恒为 0，客户端完全不动）。
            //
            // 只贴位置就够：权威位置成了新基准，时间轴仍旧由活推进负责（下一个 tick 继续采样），
            // 期间的位移差照旧按"死区/平滑/直接贴"分档处理。
            var oldState = _state;
            var posErr = hadState ? _space.Distance(_state, snapshot.OwnState) : 0f;
            var posKind = !hadState ? CorrectionKind.Bootstrap
                : posErr < _config.CorrectionDeadzone ? CorrectionKind.Deadzone
                : posErr < _config.CorrectionSnapAbove ? CorrectionKind.Blended
                : CorrectionKind.Snapped;
            _state = snapshot.OwnState;
            RebaseRenderEndpoints(oldState);   // 同上：端点跟着平移，接缝不跳
            // 首份快照（建链）：时间轴对齐到权威 tick。**只有这一种情况允许动时间轴** ——
            // 否则活推进会在下一帧把"从 0 到服务端现在"整段跨完（实测一帧采出 4705 条操作并把位置飞出地图）。
            if (!hadState) _stateTick = Math.Max(_stateTick, snapshot.Tick);
            _hasState = true;
            _version++;
            var posBlend = 0;
            if (posKind == CorrectionKind.Blended)
            {
                posBlend = StartBlend();
            }
            else if (posKind == CorrectionKind.Snapped)
            {
                ClearBlend();
            }

            UpdateRendered();
            if (posKind == CorrectionKind.Blended && posBlend == 0) posKind = CorrectionKind.Snapped;

            LastReport = new CorrectionReport(
                posKind, snapshot.Tick, (long)Math.Floor(_stateTick), _version, snapshot.AppliedSeq,
                posErr, 0, false, false, 0, 0f, 0f, Starved(Clock.TickAt(nowMs)),
                Blending ? (int)Math.Ceiling(_blendTotalTicks) : 0);
            if (posKind is CorrectionKind.Blended or CorrectionKind.Snapped or CorrectionKind.Bootstrap)
                OnCorrected?.Invoke(LastReport);
            _metrics?.OnCorrection(LastReport);
            return LastReport;
        }

        // ── 重放终点：服务端"现在"（含单程延迟）──
        var nowTick = ServerNowTick(nowMs);
        var wanted = nowTick - snapshot.Tick;
        var clamped = wanted > _config.MaxReplayTicks;
        var target = clamped ? snapshot.Tick + _config.MaxReplayTicks : nowTick;

        // ⚠️ 终点**绝不能落在链头后面**：两次快照之间客户端是按帧往前走的，
        //    `_stateTick` 往往已经比"限幅后的终点"更靠前（一次超出上限的延迟尖峰就是如此）。
        //    若把链拉回去，会同时犯两个错：
        //    ① 白扔掉已经算好的预测（下一次 AdvanceFrame 又得重算一遍），
        //    ② `predicted` 是"当前链推进到 target"——target 在过去 ⇒ 推不动 ⇒ 误差变成
        //       在两个**不同时刻**之间比较，于是一次位置本来完全一致的延迟尖峰
        //       被误判成"直接贴"（实测 25 tick 尖峰假报 2.5 格跳动）。
        //    这条与"重放起点是权威"并不冲突：重放仍然从权威状态重算，
        //    只是重算到"客户端已经到过的时刻"，而不是倒退回去。
        if (hadState && _stateTick > target) target = _stateTick;

        // ⚠️ 也不能重放到**快照那一刻之前**：延迟恢复时快照会"跑到我们时钟前面"
        //    （nowTick < snapshotTick），若把终点留在 nowTick，重放长度为 0，链头就停在
        //    快照 tick 上 —— 而 predicted 却是在一个**更早的时刻**量的 ⇒ 又是同一个时刻错配
        //    （实测报出 1.29 格的假失配 + 一次可见突跳）。至少推到快照那一刻，
        //    让"谁该在哪里"两边都算到同一个时刻。
        if (target < snapshot.Tick) target = snapshot.Tick;

        // ── ① 客户端"本来会显示"的位置：把**旧状态**推进到同一个时刻 ──
        //
        // ⚠️ 误差必须在**同一时刻**比较。早先版本直接拿"快照前的旧位置"比"重放后的新位置"，
        // 两者本来就差一帧的正常位移 —— 恒定意图下实测误差恒为 0.5 格（正好 1 tick 的位移），
        // 于是每一份快照都被判成"需要平滑"。这是指标本身错了，不是预测错了。
        var predicted = _state;
        if (hadState)
        {
            AdvanceTo(target, replay: false); // 与 render 无关，只用来取"未校正时的此刻位置"
            predicted = _state;
        }

        // ── ② Rebase：重放起点是**权威状态**，不是"当前位置往后重放" ──
        _state = snapshot.OwnState;
        _stateTick = snapshot.Tick;
        _hasState = true;
        _version++; // 改链就 +1（tick 会倒退，version 不会）

        // 同 tick 跨度的速度对比（与延迟无关）：服务端这一步走了多少 vs 本地纯预测走了多少。
        var authStepSpeed = 0f;
        if (_netCache.TryLatest(out var latest) && _netCache.TryPrevious(out var previous))
        {
            var spanTicks = latest.Tick - previous.Tick;
            if (spanTicks > 0)
            {
                authStepSpeed = _model.Distance(previous.State, latest.State)
                                * (float)(_config.TicksPerSecond / spanTicks);
            }
        }

        var predSteps = _predSteps;
        // 用"位移 ÷ 实际推进时长"：活推进是逐帧小步，不能再按"每次 = 一个 tick"折算。
        var predStepSpeed = _predSeconds > 1e-9
            ? _predAccum / (float)_predSeconds
            : 0f;
        _predAccum = 0f;
        _predSeconds = 0;
        _predSteps = 0;

        // ── ③ 重放：从权威状态推进到同一个 target ──
        var tickBefore = _stateTick;
        AdvanceTo(target, replay: true);
        // "至少重放了多少 tick"：活推进是逐帧的，重放窗口常带小数，round 会把 0.5 抹成 0。
        var replayed = (int)Math.Ceiling(_stateTick - tickBefore - 1e-9);

        // ── 分档：同一时刻的差距，才是"真失配" ──
        var err = hadState ? _model.Distance(predicted, _state) : 0f;
        var kind = !hadState ? CorrectionKind.Bootstrap
            : err < _config.CorrectionDeadzone ? CorrectionKind.Deadzone
            : err < _config.CorrectionSnapAbove ? CorrectionKind.Blended
            : CorrectionKind.Snapped;

        // ⚠️ 死区**不能**清掉正在进行的过渡：
        //    残差是"屏幕上还没消化完的偏移"，把它一次性抹平 = 一次突跳
        //    （实测：模拟每帧只走 0.17，渲染却跳了 1.34）。
        //    死区的语义是"这次没有新东西要消化"，不是"把旧的也丢掉"。
        var blendTicks = 0;
        if (kind == CorrectionKind.Blended)
        {
            blendTicks = StartBlend();
        }
        else if (kind != CorrectionKind.Deadzone)
        {
            ClearBlend(); // Snapped / Bootstrap：设计上不掩饰
        }

        UpdateRendered();

        if (kind == CorrectionKind.Blended && blendTicks == 0)
        {
            // StartBlend 因残差超上限而改判：如实报告成"直接贴"。
            kind = CorrectionKind.Snapped;
        }

        var report = new CorrectionReport(
            kind,
            snapshot.Tick,
            (long)Math.Floor(_stateTick),
            _version,
            snapshot.AppliedSeq,
            err,
            replayed,
            clamped,
            _lastReplayHadMismatch,
            predSteps,
            predStepSpeed,
            authStepSpeed,
            Starved(nowTick),
            blendTicks);
        _lastReplayHadMismatch = false;
        LastReport = report;

        if (kind is CorrectionKind.Bootstrap or CorrectionKind.Blended or CorrectionKind.Snapped)
            OnCorrected?.Invoke(report);
        _metrics?.OnCorrection(report);

        return report;
    }

    /// <summary>
    /// 用"服务端在哪个 tick 确认了这条输入"反推单程延迟：
    /// <c>observed = 快照 tick − 该输入的发送 tick</c>，做 EMA。
    ///
    /// 只要第一条被确认的输入测一次就够（每条 seq 只测第一次，否则会被"服务端一直重复
    /// 确认同一条"灌大）。没有它，重放会提前应用新意图 → 每次转向都产生一次假校正。
    /// </summary>
    private void CalibrateOneWay(long snapshotTick, ulong appliedSeq)
    {
        if (appliedSeq == 0 || appliedSeq <= _lastCalibratedSeq) return;

        // 标定要在 Acknowledge 裁剪之前做（记录还在）。
        // 记录还没到（比如输入比第一份快照晚）→ **不要**消费这次机会，下一份快照再试。
        if (!Inputs.TryGetBaseTick(appliedSeq, out var baseTick)) return;

        // 找到了就消费掉这次机会（哪怕这条记录不可用），否则会卡在同一条上一直重试。
        _lastCalibratedSeq = appliedSeq;
        if (double.IsNaN(baseTick)) return; // 发送时时钟还没同步：不可用

        // ⚠️ 这里量到的是**往返**，不是单程：
        //   baseTick = TickAt(发送时刻) 本身已经落后服务端一个单程（时钟就是这么标定的），
        //   而 snapshotTick 是"服务端应用它"的那一 tick，两者相减 = 2×单程 + 量化。
        //   不除以 2 就会把客户端多推一个单程（实测 300ms 下多推 6 tick，误差随延迟线性增长）。
        //   注意它对"客户端与服务端的墙钟偏差"是不敏感的（Δ 在相减中消掉）。
        var observed = Math.Clamp((snapshotTick - baseTick) * 0.5, 0.0, 20.0);

        // 慢速 EMA + **每次限幅，包括第一次（从 0 开始爬）**。
        // 一次赋值 2~4 tick 会让预测目标一帧跳过好几 tick —— 时间轴瞬间追完，
        // 渲染就是一次可见突跳（实测一帧跳 1.3~2.5 格）。限速后是平滑逼近。
        // 上限 0.5 tick/快照 ≈ 10 tick/秒：几秒内收敛，同时每帧目标只挪 ~0.17 tick
        // （不会变成可见突跳）。0.1 太慢 —— 900ms 单程要 18 秒才追上。
        var step = Math.Clamp((observed - _oneWayTicks) * 0.05, -0.5, 0.5);
        _oneWayTicks += step;
        // 输入生效延迟 ≈ 往返 = 2 × 单程（同一个观测量的另一种用法）。
        _inputDelayTicks = _oneWayTicks * 2.0;
        _hasOneWay = true;
    }

    /// <summary>
    /// 起一段残差平滑：从**当前屏幕位置**混合到实时权威轨迹。
    ///
    /// 过渡时长 = 残差 / 收敛速度上限（下限兜底）——所以残差越大收得越久，
    /// 但每一刻的修正速度**永远不超过上限**（看起来只像"走得快了一点"，绝不会像瞬移）。
    ///
    /// ⚠️ "要不要直接贴"由调用方按**本次的新失配 <c>err</c>** 判定（撞墙/传送），
    /// 这里**不**因为"累积残差大"改判：累积的那部分正是平滑要消化的东西。
    /// （实测：延迟尖峰恢复时屏幕累积落后 2 格、而本次 err 只有 0.38 ——
    ///   按残差改判就变成一次 2 格的可见突跳，按 err 判定则是 0.4 秒的平滑追上。）
    /// </summary>
    private int StartBlend()
    {
        // 残差 = **基座**（`_rendered`，不含 tick 间插值偏移）− 现在的模拟位置。
        //
        // 为什么不是"屏幕上的位置"（= 基座 + 插值偏移）：插值模式下屏幕位置本来就
        // **故意落后**最新状态最多一个 tick，用它播种会把这份滞后当成"待消化的残差"再算一次，
        // 于是每帧都在额外拉一把（实测校正瞬间渲染位置 0.75 → 0.00 的跳变）。
        // 插值偏移的连续性由调用方"跟着校正量平移端点"保证（见 RebaseRenderEndpoints）。
        _residual = _space.Delta(_state, _rendered);
        _blendElapsedTicks = 0;

        // ⚠️ 时长必须按**残差的实际大小**反推，不能按 err（本次的新失配）：
        //    接缝零跳变意味着残差 = 新失配 + 上一段还没消化完的偏移，可以比 err 大；
        //    按 err 反推会让每 tick 的修正量突破速度上限（实测 5.33 > 5 格/秒）。
        var magnitude = _space.Distance(_state, _rendered);

        // 静止时收得更快（站着平移最显眼）；走动时慢一点收（能混进走路里不被察觉）。
        // ⚠️ 用"最近几个 tick 有没有实际位移"判断，不要用区间平均速度 ——
        //    快照帧的区间被重放占掉，平均速度会被稀释成接近 0，于是走动被误判成静止、
        //    用了更高的收敛上限，出现超过走动上限的校正。
        var moving = _ticksSinceMotion < 3;
        var speed = Math.Max(
            moving ? _config.MaxCorrectionSpeed : _config.MaxCorrectionSpeedStopped, 1e-3);

        // 时长由速度上限反推 ⇒ 速度上限永远成立（不会被别的上限顶破）。
        var wantTicks = magnitude / (speed * _config.TickSeconds);
        var minTicks = _config.CorrectionBlendMinMs / _config.TickMs;

        _blendTotalTicks = Math.Max(wantTicks, minTicks);
        return (int)Math.Ceiling(_blendTotalTicks);
    }

    private void ClearBlend()
    {
        _blendTotalTicks = 0;
        _blendElapsedTicks = 0;
        _blendWeight = 0f;
    }

    /// <summary>刷新渲染位置：<c>Lerp(模拟状态, 屏幕起点, w)</c>，w 随 tick 推进从 1 衰减到 0。</summary>
    private void UpdateRendered()
    {
        if (_blendTotalTicks <= 0)
        {
            _rendered = _state;
            _hasRendered = true;
            _blendWeight = 0f;
            UpdateRenderLead();
            return;
        }

        var k = (float)Math.Clamp(1.0 - _blendElapsedTicks / _blendTotalTicks, 0.0, 1.0);
        if (k <= 0f)
        {
            ClearBlend();
            _rendered = _state;
            _hasRendered = true;
            UpdateRenderLead();
            return;
        }

        _blendWeight = k;
        // 常数速率的残差衰减：既不停顿也不倒退。
        _rendered = _space.AddDelta(_state, _residual, k);
        _hasRendered = true;
        UpdateRenderLead();
    }

    /// <summary>
    /// 校正/回退把模拟状态从 <paramref name="oldState"/> 挪到了当前 <see cref="_state"/>，
    /// 把**插值端点** <see cref="_lastStepFrom"/> 也挪同样的量。
    ///
    /// 这样 Δ = S(n)−S(n−1) 保持不变 ⇒ tick 间插值偏移不变 ⇒ 渲染位置在接缝处**不跳**，
    /// 校正量全部交给残差平滑（<see cref="Residual"/>）去消化。端上逐帧实测对比过三种做法
    /// （清零 / 保留旧值 / 跟着平移），只有跟着平移是零跳变的。
    /// </summary>
    private void RebaseRenderEndpoints(TState oldState)
    {
        if (!_config.TickIndexedIntents || !_hasLastStep) return;
        var shift = _space.Delta(oldState, _state);
        _lastStepFrom = _space.AddDelta(_lastStepFrom, shift, 1f);
    }

    /// <summary>
    /// 【序号锚定模式】tick 之间的**渲染插值偏移**：模拟在整 tick 上走，渲染在帧上要连续。
    ///
    /// 记 <c>S(n-1)</c> = <see cref="_lastStepFrom"/>（上一条 tick 的状态）、<c>S(n)</c> = <see cref="_state"/>
    /// （最新 tick 的状态）、<c>r</c> = <see cref="_renderRemainder"/>（帧余量，0~1 个 tick）。
    /// 采样点相对 <c>S(n-1)</c> 的位置 <c>α = 1 + r - 延迟</c>：
    /// <list type="bullet">
    /// <item>延迟 = 1（默认）⇒ <c>α = r</c> ⇒ 渲染在 <b>两个已知 tick 状态之间</b>插值，
    ///   端点都是已知的，所以逐帧位移严格线性、绝不"猜错再回抽"（代价：晚约一个 tick）；</item>
    /// <item>延迟 = 0 ⇒ <c>α = 1 + r</c> ⇒ 从最新状态往前**外推**半个 tick（零延迟，但会回抽）。</item>
    /// </list>
    /// 返回的是**相对 <c>S(n)</c> 的偏移**（插值时是负的 = 滞后），只碰 <see cref="_renderLead"/>，
    /// 绝不进模拟 —— 回流会污染"同序号状态"的比较。
    /// </summary>
    private void UpdateRenderLead()
    {
        if (!_config.TickIndexedIntents || !_hasLastStep)
        {
            _renderLead = default;
            return;
        }

        var delay = _config.OwnRenderDelayTicks;
        var alpha = 1.0 + _renderRemainder - delay;
        // 不越过"最新 tick 状态"（默认插值模式）；外推模式允许最多再推半个 tick。
        var maxAlpha = delay >= 1.0 ? 1.0 : 2.0 - delay;
        if (alpha < 0) alpha = 0;
        if (alpha > maxAlpha) alpha = maxAlpha;

        // 偏移 = (S(n-1) + α·Δ) - S(n) = (α - 1)·Δ
        _renderLead = _space.AddDelta(default, _space.Delta(_lastStepFrom, _state), (float)(alpha - 1.0));
    }

    /// <summary>
    /// 【序号锚定和解】直接比"**我执行完第 N 条操作之后的状态**"和"**服务端执行完第 N 条操作之后的状态**"。
    ///
    /// 为什么这样才能不猜：两边都是"同一个操作前缀 + 同样的 tick 数"（一条操作 = 一个 tick），
    /// 所以这两个状态本来就该相等 —— 与"各自在墙钟的哪一刻"无关，**根本不需要知道在途时间**。
    /// 差在死区内 ⇒ 一致，链一动不动（我在途的那些操作不在比较范围内，自然不会制造假误差）；
    /// 差更大 ⇒ 真分歧，把链接到服务端那个状态，再用**我自己的节拍**把后续操作重跑回链头。
    /// </summary>
    private CorrectionReport? ReconcileByOpIndex(in NetSnapshot<TState> snapshot, long nowMs, bool hadState)
    {
        if (!hadState || snapshot.AppliedSeq == 0) return null;

        var found = -1;
        for (var i = _opRing.Count - 1; i >= 0; i--)
        {
            if (_opRing[i].Index != snapshot.AppliedSeq) continue;
            found = i;
            break;
        }

        // 这条操作已经不在回看范围里（延迟超出环长）→ 交回旧路径，并记账（见 ReplayClamped 的用法）
        if (found < 0) return null;

        var op = _opRing[found];
        var err = _space.Distance(op.StateAfter, snapshot.OwnState);

        // 诊断：同一 ack 区间内"我走了多少 vs 服务端走了多少"（判"速度差"还是"方向差"）
        var clientMove = 0f;
        var serverMove = 0f;
        var driftOps = 0;
        var clientSteps = 0;
        if (_hasDiag && snapshot.AppliedSeq > _diagAck)
        {
            driftOps = snapshot.AppliedSeq - _diagAck > int.MaxValue
                ? int.MaxValue
                : (int)(snapshot.AppliedSeq - _diagAck);
            serverMove = _space.Distance(_diagState, snapshot.OwnState);
            // ⚠️ 客户端这一段"走了多少"必须用**每条操作自己的 StepDistance** 累加：
            //    用"两个环状态之差"会被中途的校正 rebase 污染（实测 1 个 op 却量到 1.1 格）。
            for (var i = 0; i < _opRing.Count; i++)
            {
                var rec = _opRing[i];
                if (rec.Index <= _diagAck || rec.Index > snapshot.AppliedSeq) continue;
                clientMove += rec.StepDistance;
                if (rec.StepDistance > 0f || !rec.Discrete) clientSteps++;
            }
        }
        _hasDiag = true;
        _diagAck = snapshot.AppliedSeq;
        _diagState = snapshot.OwnState;
        var kind = err < _config.CorrectionDeadzone ? CorrectionKind.Deadzone
            : err < _config.CorrectionSnapAbove ? CorrectionKind.Blended
            : CorrectionKind.Snapped;

        var head = _stateTick;
        var replayed = 0;

        if (kind != CorrectionKind.Deadzone)
        {
            var oldState = _state;
            var resumeTick = op.Tick + 1.0;   // "执行完第 N 条之后" = 我自己的下一个 tick
            _state = snapshot.OwnState;
            _stateTick = resumeTick;
            // 链被 rebase 了：**把插值端点跟着校正量一起平移**（Δ 不变 ⇒ 插值偏移不变 ⇒ 连续）。
            //
            // 三种做法都试过（端到端逐帧追出来的）：
            //   · 端点清零（贴到最新状态）：那一帧渲染比插值点靠前将近一个 tick，下一帧再回来
            //     ⇒ 校正频繁时 offsetDist 在 0.03↔0.34 之间跳、dx 在 ±0.2 来回 —— 我自己引入的抖动；
            //   · 端点保留旧值（不平移）：Δ 会变成"校正量 + 一步"，插值偏移被放大
            //     （单测实测渲染位置 0.75 → 0.00 的跳变）；
            //   · 跟着平移（本实现）：Δ 不变，渲染位置只由残差平滑负责，接缝零跳变。
            _version++;                       // 改链就 +1（tick 也可能回退，version 不会）
            AdvanceTo(head, replay: true);    // 用我自己的操作序列/节拍重跑回链头
            replayed = (int)Math.Ceiling(_stateTick - resumeTick - 1e-9);
            RebaseRenderEndpoints(oldState);  // 在**重放之后**平移：让端点跟最终状态对齐
        }

        // 渲染层照旧：残差从"当前屏幕位置"播种，若干 tick 内收掉（残差永不回流进模拟）。
        var blendTicks = 0;
        if (kind == CorrectionKind.Blended)
        {
            blendTicks = StartBlend();
        }
        else if (kind == CorrectionKind.Snapped)
        {
            ClearBlend();
        }

        UpdateRendered();
        if (kind == CorrectionKind.Blended && blendTicks == 0) kind = CorrectionKind.Snapped;

        var report = new CorrectionReport(
            kind, snapshot.Tick, (long)Math.Floor(_stateTick), _version, snapshot.AppliedSeq,
            err, replayed, false, false, 0, 0f, 0f, Starved(Clock.TickAt(nowMs)),
            Blending ? (int)Math.Ceiling(_blendTotalTicks) : 0)
        {
            ClientMove = clientMove,
            ServerMove = serverMove,
            DriftOps = driftOps,
            ClientSteps = clientSteps,
        };
        LastReport = report;
        if (kind is CorrectionKind.Blended or CorrectionKind.Snapped) OnCorrected?.Invoke(report);
        _metrics?.OnCorrection(report);
        return report;
    }

    private void AdvanceTo(double targetTick, bool replay)
    {
        if (!_hasState || !Clock.HasSync) return;
        var delta = targetTick - _stateTick;
        if (delta <= 1e-9) return;

        // 整 tick 部分：每个 tick 用服务端 tick 长推进（重放才能逐位复现服务端）。
        var whole = (int)Math.Floor(delta + 1e-9);
        for (var i = 0; i < whole; i++)
        {
            if (_config.TickIndexedIntents && !replay && _hasCurrentIntent)
            {
                // 序号锚定模式：**每个 tick 采样一条编号操作**（意图没变也发一条）。
                // 序号只是自增 uint64（去重/排序/ACK 用），与 tick 无关；
                // "一条操作 = 一个 tick"才是让两边"同一条操作之后的状态"可比的前提。
                _currentOpIndex = ++_opSeq;
                Inputs.Record(_currentOpIndex, _epoch, _stateTick, _stateTick, _currentIntent);
            }

            if (_config.TickIndexedIntents && !replay)
            {
                // 记下这一步的起点，渲染层要用它把"帧余量"外推成连续位移。
                _lastStepFrom = _state;
                _hasLastStep = true;
            }

            StepOnce(_config.TickSeconds, replay);
            _stateTick = Math.Floor(_stateTick + 1e-9) + 1.0;
        }

        // 小数部分（帧余量）。
        var frac = targetTick - _stateTick;
        if (_config.TickIndexedIntents && !replay && frac >= 0)
        {
            // ⚠️ 余量**每帧都要更新**，包括"正好落在整 tick 上"（frac≈0）的情况：
            //    早先只在 frac>1e-6 时才写，落到整 tick 的那一帧会**沿用上一帧的余量**
            //    ⇒ 渲染位置多外推接近一整步（该帧位移翻倍），下一帧余量回落到真实值
            //    ⇒ 位置又往回抽。端上逐帧实测：直线行走 599 帧里 30 帧 ΔX<0（最深 -0.14 格
            //    ＝倒退一整步），逐帧位移在 0.053~0.168 之间乱跳（约 3~10 格/秒）——
            //    视觉上就是"一卡一卡"，而且**和服务器校正无关**（有些帧 err=0 照样回抽）。
            _renderRemainder = frac;
            if (frac <= 1e-6) return;   // 没有余量可模拟（整 tick 模式本来就只走整 tick）
        }
        if (frac > 1e-6)
        {
            if (_config.TickIndexedIntents)
            {
                // ⚠️ 序号锚定模式**不模拟余量**：模拟只走整 tick（和服务端同一步长），
                //    否则"我在 tick 中间转向"这件事服务端永远复现不出来（同序号状态就不相等了）。
                //    余量交给渲染层外推（见 UpdateRendered / RenderedState）。
                return;
            }

            StepOnce(frac * _config.TickSeconds, replay);
            _stateTick = targetTick;
        }
    }

    private void StepOnce(double dtSeconds, bool replay)
    {
        // 意图按"当前所在的服务端 tick"取；没有记录就用当前意图兜底。
        Inputs.TryActionAtTick(_stateTick, out var action);

        var next = _state;
        var used = _model.Step(ref next, action, dtSeconds, _version);
        if (used != _version)
        {
            // 预测器对不上版本 = 它基于重放前的旧基准算的：丢弃并重算一次。
            next = _state;
            used = _model.Step(ref next, action, dtSeconds, _version);
            if (used != _version)
            {
                // 仍不一致：采纳结果但显式记账（调用方应当报警，别静默吞掉）。
                VersionMismatchCount++;
                _lastReplayHadMismatch = true;
            }
        }

        var stepDistance = _model.Distance(_state, next);
        _lastStepDistance = stepDistance;
        if (stepDistance > 0.001f) _ticksSinceMotion = 0;
        else _ticksSinceMotion++;

        if (!replay)
        {
            _predAccum += stepDistance;
            _predSeconds += dtSeconds;
            _predSteps++;
            // 残差平滑只在"真实时间推进"时衰减；重放在一帧内瞬时完成，不吃过渡时间。
            if (_blendTotalTicks > 0) _blendElapsedTicks += dtSeconds / _config.TickSeconds;
        }

        _state = next;
        _version++;

        // 序号锚定模式：把"执行完这一条操作之后我自己的状态"存进小环 ——
        // 这就是和解时要拿来和"服务端执行完同一条操作之后的状态"直接比的东西。
        if (_config.TickIndexedIntents && !replay && _hasCurrentIntent)
        {
            _opRing.Add(new OpRecord(
                _currentOpIndex, (long)Math.Floor(_stateTick + 1e-9), next, _currentIntent,
                StepDistance: stepDistance));
            if (_opRing.Count > _config.OpHistoryCapacity) _opRing.RemoveAt(0);
        }
    }
}
