using System.Collections.Concurrent;
using System.Linq;
using Google.Protobuf;
using Starve.Game.V1;
using Starve.Protocol.Pomelo;

namespace Starve.Protocol.World;

/// <summary>天气摘要（帧内粗粒度平均，渲染层据此出雨/雾）。</summary>
public sealed record WeatherSummary(
    float Rain,
    float Fog,
    float WindDirX,
    float WindDirY,
    float WindSpeed);

/// <summary>实体视图：entityId + 组件原始字节（按名懒解析）+ 每个组件"最后到达的世界 tick"。</summary>
public sealed class EntityView
{
    public ulong EntityId { get; }
    public System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> Components { get; } = new();

    /// <summary>
    /// 组件名 → 最后一次真正被服务端下发的世界 tick。
    ///
    /// 为什么需要它：增量快照只带**脏**组件。玩家停下后服务端 MoveSystem 提前 return，
    /// 不再标脏 Position/Moveable，于是这两个组件会一直停留在"停下那一刻"的值——
    /// 数据本身是旧的，但每次增量都会让 Revision +1。校正逻辑如果只看"有没有值"，
    /// 就会拿着这份冻结的旧位置反复收敛，表现为停下后回拉抖动。
    /// 用这个 tick 就能判断"这份位置是新鲜的，还是陈旧快照"。
    /// </summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, long> ComponentTicks { get; } = new();

    public EntityView(ulong entityId) => EntityId = entityId;

    /// <summary>
    /// 组件名 → 已解析对象（缓存）。
    ///
    /// 为什么必须缓存：<see cref="Get{T}"/> 原先**每次调用都 ParseFrom**，
    /// 而它在每帧、每实体的循环里被调用（GameRoot 与 EntityLayer3D 共 80 处），
    /// 每次解析都会分配一个新 protobuf 对象。实测 500 可见实体 × 3 组件 ×
    /// 60fps ≈ 9 万次分配/秒，导致 Gen0 每秒回收 12.7 次、GC 暂停 10.8ms/秒
    /// （60FPS 的帧预算才 16.7ms），表现为走动时周期性顿挫。
    ///
    /// 缓存键带上**原始字节的引用**：组件更新时 Components[key] 会被换成
    /// 新数组，引用不同即失效，因此不需要在写入侧做任何失效通知。
    /// </summary>
    private readonly ConcurrentDictionary<string, object> _parsedCache = new();

    public T? Get<T>(string component, MessageParser<T> parser) where T : class, IMessage<T>
    {
        if (!Components.TryGetValue(component, out var data)) return null;
        // 快路径：同一份字节已经解析过，直接复用。
        if (_parsedCache.TryGetValue(component, out var cached) &&
            cached is CachedComponent hit && ReferenceEquals(hit.Raw, data))
        {
            return (T)hit.Value;
        }
        var value = parser.ParseFrom(data);
        _parsedCache[component] = new CachedComponent(data, value);
        return value;
    }

    /// <summary>缓存条目：记住"由哪份原始字节解析而来"，用于判断是否需要重解析。</summary>
    private readonly record struct CachedComponent(byte[] Raw, object Value);

    /// <summary>该组件最后一次下发的世界 tick；从未收到返回 -1。</summary>
    public long ComponentTick(string component) =>
        ComponentTicks.TryGetValue(component, out var tick) ? tick : -1;
}

/// <summary>世界数据：消费全量快照 + 每 tick 增量，维护本地权威实体表（阶段 0 最小实现）。</summary>
public sealed class WorldService
{
    private const int EventHistoryLimit = 2048;
    private readonly ConcurrentDictionary<ulong, EntityView> _entities = new();
    private readonly HashSet<ulong> _publishedEventIds = new();
    private readonly Queue<ulong> _eventHistory = new();
    private readonly HashSet<ActionOutcomeKey> _publishedOutcomes = new();
    private readonly Queue<ActionOutcomeKey> _outcomeHistory = new();
    private TaskCompletionSource _snapshotReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _revision;

    public event Action<ulong, ulong, long>? InputAcknowledged;
    public event Action<ActionOutcome>? ActionOutcomeReceived;
    public event Action<WorldEvent>? WorldEventReceived;
    public event Action<WorldEvent, CombatImpactEvent>? CombatImpactReceived;
    public event Action<WorldEvent, HealthChangedEvent>? HealthChangedReceived;

    /// <summary>静态配置（登录后推送 world.config）。</summary>
    public GameConfig? Config { get; private set; }

    /// <summary>地图配置（从 Config.Map 取，null = 还没到）。</summary>
    public MapConfig? Map => Config?.Map;

    /// <summary>昼夜光照（0..1，来自 DayCycle.Light）。</summary>
    public float DayLight { get; private set; } = 0.5f;

    /// <summary>世界 tick（来自 Snapshot/SnapshotDelta.tick；插值/预测对齐用）。</summary>
    public long WorldTick { get; private set; }

    public ulong InputEpoch { get; private set; }

    /// <summary>服务端已应用的最大命令 seq（仅当前连接）。</summary>
    public ulong LastAcceptedSeq { get; private set; }

    /// <summary>季节（Season 枚举值，来自 WeatherState）。</summary>
    public int Season { get; private set; }

    /// <summary>最近一帧天气（平均雨/雾 + 风向风速），null = 还没收到。</summary>
    public WeatherSummary? Weather { get; private set; }

    /// <summary>原始天气帧（含按格雨/雾），供渲染层做按格雾/雨。</summary>
    public WeatherFrame? WeatherFrame { get; private set; }

    /// <summary>世界版本号：任何快照/增量/配置变化都会 +1（渲染层轮询用）。</summary>
    public int Revision => Volatile.Read(ref _revision);

    /// <summary>
    /// 以 <see cref="Revision"/> 为序号锁的**原子读**：回调里的多次读取保证不会被
    /// "应用新消息"打断（期间来了新快照就整体重读）。
    ///
    /// 为什么必须有这个 API：推送是在**网络线程**上直接改世界的
    /// （<see cref="Session.OnPush"/> → <see cref="HandleMessage"/>，不是排队到主线程），
    /// 而主线程渲染/预测要读"自己的位置 + 组件 tick + ack"。分几次读就会配出
    /// 「位置来自消息 m、ack 来自消息 m+1」—— 和解拿它做序号锚定比较时整体偏一个 tick，
    /// 于是**每份快照都要校正一次**（实测恒定 0.5 格 = 1 个 tick 的位移，转向处翻倍到 1.0+），
    /// 表现就是"走着走着时不时卡一下"。回调必须是**纯读**（无副作用），否则重读会重复副作用。
    /// </summary>
    public T ReadAtomic<T>(Func<T> read)
    {
        while (true)
        {
            var rev = Revision;
            var value = read();
            if (Revision == rev) return value;
        }
    }

    public IReadOnlyDictionary<ulong, EntityView> Entities => _entities;
    public int Count => _entities.Count;

    /// <summary>登录后等待第一份全量快照。</summary>
    public Task WaitForSnapshotAsync(CancellationToken ct = default) =>
        _snapshotReady.Task.WaitAsync(ct);

    public void HandleMessage(PomeloMessage msg)
    {
        if (msg.Route == Routes.Snapshot)
        {
            var snap = Snapshot.Parser.ParseFrom(msg.Data);
            if (snap.InputEpoch == 0) return;
            ResetEventScope();
            _entities.Clear();
            foreach (var es in snap.Entities) Add(es, (long)snap.Tick);
            ApplyWorldState(
                snap.DayCycle, snap.Weather, snap.Tick, snap.InputEpoch, snap.LastAcceptedSeq);
            _snapshotReady.TrySetResult();
            Bump();
        }
        else if (msg.Route == Routes.SnapshotDelta)
        {
            var delta = SnapshotDelta.Parser.ParseFrom(msg.Data);
            if (InputEpoch != 0 && delta.InputEpoch != InputEpoch) return;
            if (delta.Tick != 0 && WorldTick != 0 && delta.Tick < (ulong)WorldTick) return;
            foreach (var es in delta.Entities) Add(es, (long)delta.Tick);
            foreach (var id in delta.RemovedEntities) _entities.TryRemove(id, out _);
            foreach (var rc in delta.RemovedComponents)
            {
                if (_entities.TryGetValue(rc.EntityId, out var view))
                {
                    foreach (var name in rc.Components) view.Components.TryRemove(name, out _);
                }
            }
            ApplyWorldState(
                delta.DayCycle, delta.Weather, delta.Tick, delta.InputEpoch, delta.LastAcceptedSeq);
            Bump();
            PublishEvents(delta.Events);
        }
        else if (msg.Route == Routes.Config)
        {
            Config = GameConfig.Parser.ParseFrom(msg.Data);
            Bump();
        }
        else if (msg.Route == Routes.ActionOutcome)
        {
            PublishOutcome(ActionOutcome.Parser.ParseFrom(msg.Data));
        }
        else if (msg.Route == Routes.WeatherFrame)
        {
            var frame = Starve.Game.V1.WeatherFrame.Parser.ParseFrom(msg.Data);
            WeatherFrame = frame;
            Weather = frame.Cells.Count > 0
                ? new WeatherSummary(
                    (float)frame.Cells.Average(c => c.Rain),
                    (float)frame.Cells.Average(c => c.Fog),
                    frame.WindDirX,
                    frame.WindDirY,
                    frame.WindSpeed)
                : null;
            Bump();
        }
    }

    private void ApplyWorldState(
        DayCycle? dayCycle,
        WeatherState? weather,
        ulong tick,
        ulong inputEpoch,
        ulong lastAcceptedSeq)
    {
        WorldTick = (long)tick;
        InputEpoch = inputEpoch;
        LastAcceptedSeq = lastAcceptedSeq;
        if (dayCycle is not null)
        {
            DayLight = dayCycle.Light;
        }
        if (weather is not null) Season = (int)weather.Season;
        InputAcknowledged?.Invoke(inputEpoch, lastAcceptedSeq, WorldTick);
    }

    private void Add(EntityState es, long serverTick)
    {
        // 增量只带 dirty 组件：合并进已有视图，绝不整体替换（否则其他组件被冲掉）
        if (!_entities.TryGetValue(es.EntityId, out var view))
        {
            view = new EntityView(es.EntityId);
        }
        foreach (var c in es.Components)
        {
            view.Components[c.Component] = c.Data.ToByteArray();
            // 打上"这一份值是哪一 tick 下发的"：陈旧判定靠它，见 EntityView.ComponentTicks。
            view.ComponentTicks[c.Component] = serverTick;
        }
        _entities[es.EntityId] = view;
    }

    private void PublishEvents(IEnumerable<WorldEvent> events)
    {
        foreach (var worldEvent in events)
        {
            if (worldEvent.EventId != 0 && !_publishedEventIds.Add(worldEvent.EventId)) continue;
            if (worldEvent.EventId != 0)
            {
                _eventHistory.Enqueue(worldEvent.EventId);
                if (_eventHistory.Count > EventHistoryLimit)
                {
                    _publishedEventIds.Remove(_eventHistory.Dequeue());
                }
            }

            WorldEventReceived?.Invoke(worldEvent);
            if (worldEvent.Outcome is { } outcome)
            {
                PublishOutcome(outcome);
            }
            else if (worldEvent.Impact is { } impact)
            {
                CombatImpactReceived?.Invoke(worldEvent, impact);
            }
            else if (worldEvent.HealthChanged is { } healthChanged)
            {
                HealthChangedReceived?.Invoke(worldEvent, healthChanged);
            }
        }
    }

    private void PublishOutcome(ActionOutcome outcome)
    {
        var key = new ActionOutcomeKey(
            outcome.EntityId,
            outcome.ActionId,
            outcome.RequestId,
            outcome.Result,
            outcome.Tick);
        if (!_publishedOutcomes.Add(key)) return;
        _outcomeHistory.Enqueue(key);
        if (_outcomeHistory.Count > EventHistoryLimit)
        {
            _publishedOutcomes.Remove(_outcomeHistory.Dequeue());
        }
        ActionOutcomeReceived?.Invoke(outcome);
    }

    private void ResetEventScope()
    {
        _publishedEventIds.Clear();
        _eventHistory.Clear();
        _publishedOutcomes.Clear();
        _outcomeHistory.Clear();
    }

    private readonly record struct ActionOutcomeKey(
        ulong EntityId,
        ulong ActionId,
        ulong RequestId,
        ActionOutcomeResult Result,
        long Tick);

    private void Bump() => Interlocked.Increment(ref _revision);
}
