using System.Collections.Generic;
using Starve.Game.V1;

namespace GodotClient.Game;

/// <summary>伤害表现端口；非 HIT 结果保留独立适配点，绝不降级为受击动画。</summary>
public interface IImpactPresentationSink
{
    void PlayHit(ulong targetEntity);
    void CorrectPredictedHit(ulong targetEntity, CombatImpactResult result);
    void PresentNonHit(ulong targetEntity, CombatImpactResult result);
}

/// <summary>瞬时战斗影响表现：预测与权威事件确认、纠错以及 event_id 幂等。</summary>
public sealed class ImpactPresentationController
{
    private const int EventHistoryLimit = 256;
    private readonly IImpactPresentationSink _sink;
    private readonly HashSet<ImpactKey> _predicted = new();
    private readonly HashSet<ImpactKey> _appliedImpacts = new();
    private readonly Queue<ImpactKey> _impactHistory = new();
    private readonly HashSet<ulong> _eventIds = new();
    private readonly Queue<ulong> _eventHistory = new();

    public ImpactPresentationController(IImpactPresentationSink sink) => _sink = sink;

    public void PredictHit(ulong sourceActionId, ulong targetEntity)
    {
        var key = new ImpactKey(sourceActionId, targetEntity);
        if (sourceActionId != 0 && _appliedImpacts.Contains(key)) return;
        if (!_predicted.Add(key)) return;
        _sink.PlayHit(targetEntity);
    }

    public void Apply(WorldEvent worldEvent, CombatImpactEvent impact)
    {
        if (worldEvent.EventId != 0 && !_eventIds.Add(worldEvent.EventId)) return;
        if (worldEvent.EventId != 0)
        {
            _eventHistory.Enqueue(worldEvent.EventId);
            if (_eventHistory.Count > EventHistoryLimit)
            {
                _eventIds.Remove(_eventHistory.Dequeue());
            }
        }

        var key = new ImpactKey(impact.SourceActionId, impact.TargetEntity);
        if (impact.SourceActionId != 0 && !_appliedImpacts.Add(key)) return;
        if (impact.SourceActionId != 0)
        {
            _impactHistory.Enqueue(key);
            if (_impactHistory.Count > EventHistoryLimit)
            {
                _appliedImpacts.Remove(_impactHistory.Dequeue());
            }
        }
        var predicted = _predicted.Remove(key);
        if (impact.Result == CombatImpactResult.Hit)
        {
            if (!predicted) _sink.PlayHit(impact.TargetEntity);
            return;
        }

        if (predicted)
        {
            _sink.CorrectPredictedHit(impact.TargetEntity, impact.Result);
        }
        if (impact.Result is CombatImpactResult.Miss or
            CombatImpactResult.Blocked or
            CombatImpactResult.Immune)
        {
            _sink.PresentNonHit(impact.TargetEntity, impact.Result);
        }
    }

    private readonly record struct ImpactKey(ulong SourceActionId, ulong TargetEntity);
}
