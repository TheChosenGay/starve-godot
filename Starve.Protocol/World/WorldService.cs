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

/// <summary>实体视图：entityId + 组件原始字节（按名懒解析）。</summary>
public sealed class EntityView
{
    public ulong EntityId { get; }
    public System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> Components { get; } = new();

    public EntityView(ulong entityId) => EntityId = entityId;

    public T? Get<T>(string component, MessageParser<T> parser) where T : class, IMessage<T> =>
        Components.TryGetValue(component, out var data) ? parser.ParseFrom(data) : null;
}

/// <summary>世界数据：消费全量快照 + 每 tick 增量，维护本地权威实体表（阶段 0 最小实现）。</summary>
public sealed class WorldService
{
    private readonly ConcurrentDictionary<ulong, EntityView> _entities = new();
    private TaskCompletionSource _snapshotReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _revision;

    public event Action<ulong, ulong, long>? InputAcknowledged;

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
            _entities.Clear();
            foreach (var es in snap.Entities) Add(es);
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
            foreach (var es in delta.Entities) Add(es);
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
        }
        else if (msg.Route == Routes.Config)
        {
            Config = GameConfig.Parser.ParseFrom(msg.Data);
            Bump();
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

    private void Add(EntityState es)
    {
        // 增量只带 dirty 组件：合并进已有视图，绝不整体替换（否则其他组件被冲掉）
        if (!_entities.TryGetValue(es.EntityId, out var view))
        {
            view = new EntityView(es.EntityId);
        }
        foreach (var c in es.Components)
        {
            view.Components[c.Component] = c.Data.ToByteArray();
        }
        _entities[es.EntityId] = view;
    }

    private void Bump() => Interlocked.Increment(ref _revision);
}
