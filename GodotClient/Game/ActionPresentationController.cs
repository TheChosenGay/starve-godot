using System;
using System.Collections.Generic;
using System.Linq;
using Starve.Game.V1;

namespace GodotClient.Game;

/// <summary>动作表现端口。实现只负责把动作类型适配到具体视觉，不持有网络状态。</summary>
public interface IActionPresentationSink
{
    void Apply(ulong entityId, ActionKind kind);
    void Finish(ulong entityId);
    void Cancel(ulong entityId);
    void Death(ulong entityId);
}

public readonly record struct ActionPresentationStatus(
    bool Predicted,
    ActionKind Kind,
    ulong ActionId,
    ActionPhase Phase);

/// <summary>
/// 实体动作表现的唯一状态机：接收本地预测和权威 ActionState，负责去重、替换与取消。
/// 权威动作只由组件移除结束；短超时仅约束尚未被服务端确认的预测。
/// </summary>
public sealed class ActionPresentationController
{
    public const long DefaultPredictionTimeoutMs = 500;
    private const int OutcomeHistoryLimit = 256;

    private readonly IActionPresentationSink _sink;
    private readonly Func<long> _nowMs;
    private readonly long _predictionTimeoutMs;
    private readonly Dictionary<ulong, Entry> _entries = new();
    private readonly Dictionary<ulong, Suppression> _suppressions = new();
    private readonly HashSet<OutcomeKey> _processedOutcomes = new();
    private readonly Queue<OutcomeKey> _outcomeHistory = new();

    public ActionPresentationController(
        IActionPresentationSink sink,
        Func<long> nowMs,
        long predictionTimeoutMs = DefaultPredictionTimeoutMs)
    {
        _sink = sink;
        _nowMs = nowMs;
        _predictionTimeoutMs = predictionTimeoutMs;
    }

    public void Predict(ulong entityId, ActionKind kind)
    {
        _entries[entityId] = new Entry(
            Predicted: true,
            Kind: kind,
            ActionId: 0,
            Phase: ActionPhase.Unspecified,
            ExpiresAtMs: checked(_nowMs() + _predictionTimeoutMs));
        _sink.Apply(entityId, kind);
    }

    public void Apply(ulong entityId, ActionState state)
    {
        var hasCurrent = _entries.TryGetValue(entityId, out var current);
        if (_suppressions.TryGetValue(entityId, out var suppression))
        {
            if (suppression.ActionId == state.ActionId &&
                suppression.Kind == state.Kind)
            {
                _entries[entityId] = AuthoritativeEntry(state);
                return;
            }
            _suppressions.Remove(entityId);
        }

        var shouldRestart = !hasCurrent ||
                            current.Kind != state.Kind ||
                            (!current.Predicted && current.ActionId != state.ActionId);
        _entries[entityId] = AuthoritativeEntry(state);
        if (shouldRestart) _sink.Apply(entityId, state.Kind);
    }

    public void Remove(ulong entityId)
    {
        var wasSuppressed = _suppressions.Remove(entityId);
        if (!_entries.Remove(entityId)) return;
        if (!wasSuppressed) _sink.Cancel(entityId);
    }

    public void ObserveAbsent(ulong entityId)
    {
        if (_entries.TryGetValue(entityId, out var current) && current.Predicted) return;
        Remove(entityId);
    }

    public void CancelPrediction(ulong entityId)
    {
        if (!_entries.TryGetValue(entityId, out var current) || !current.Predicted) return;
        _entries.Remove(entityId);
        _sink.Cancel(entityId);
    }

    public void CancelForMovement(ulong entityId)
    {
        if (!_entries.TryGetValue(entityId, out var current)) return;
        if (current.Predicted)
        {
            _entries.Remove(entityId);
        }
        else
        {
            var suppression = new Suppression(current.ActionId, current.Kind);
            if (_suppressions.TryGetValue(entityId, out var existing) && existing == suppression) return;
            _suppressions[entityId] = suppression;
        }
        _sink.Cancel(entityId);
    }

    public void ApplyOutcome(ActionOutcome outcome)
    {
        var key = new OutcomeKey(
            outcome.EntityId,
            outcome.ActionId,
            outcome.RequestId,
            outcome.Result,
            outcome.Tick);
        if (!_processedOutcomes.Add(key)) return;
        _outcomeHistory.Enqueue(key);
        if (_outcomeHistory.Count > OutcomeHistoryLimit)
        {
            _processedOutcomes.Remove(_outcomeHistory.Dequeue());
        }

        if (outcome.Result == ActionOutcomeResult.Rejected)
        {
            if (_entries.TryGetValue(outcome.EntityId, out var rejected) && rejected.Predicted)
            {
                _entries.Remove(outcome.EntityId);
                _sink.Cancel(outcome.EntityId);
            }
            return;
        }
        if (outcome.Result is not (ActionOutcomeResult.Completed or ActionOutcomeResult.Canceled))
        {
            return;
        }

        var hasCurrent = _entries.TryGetValue(outcome.EntityId, out var current);
        if (hasCurrent &&
            !current.Predicted &&
            current.ActionId != outcome.ActionId)
        {
            return;
        }

        var suppression = new Suppression(outcome.ActionId, outcome.Kind);
        if (_suppressions.TryGetValue(outcome.EntityId, out var existing) && existing == suppression) return;
        _suppressions[outcome.EntityId] = suppression;

        if (!hasCurrent) return;
        if (current.Predicted) _entries.Remove(outcome.EntityId);
        if (outcome.Result == ActionOutcomeResult.Completed)
        {
            _sink.Finish(outcome.EntityId);
        }
        else if (outcome.Reason == ActionOutcomeReason.Dead)
        {
            _sink.Death(outcome.EntityId);
        }
        else
        {
            _sink.Cancel(outcome.EntityId);
        }
    }

    public void Tick()
    {
        var now = _nowMs();
        foreach (var entityId in _entries
                     .Where(pair => pair.Value.Predicted && pair.Value.ExpiresAtMs <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _entries.Remove(entityId);
            _sink.Cancel(entityId);
        }
    }

    public ActionPresentationStatus? StatusOf(ulong entityId) =>
        _entries.TryGetValue(entityId, out var entry)
            ? new ActionPresentationStatus(entry.Predicted, entry.Kind, entry.ActionId, entry.Phase)
            : null;

    private readonly record struct Entry(
        bool Predicted,
        ActionKind Kind,
        ulong ActionId,
        ActionPhase Phase,
        long ExpiresAtMs);

    private readonly record struct Suppression(ulong ActionId, ActionKind Kind);
    private readonly record struct OutcomeKey(
        ulong EntityId,
        ulong ActionId,
        ulong RequestId,
        ActionOutcomeResult Result,
        long Tick);

    private static Entry AuthoritativeEntry(ActionState state) => new(
        Predicted: false,
        Kind: state.Kind,
        ActionId: state.ActionId,
        Phase: state.Phase,
        ExpiresAtMs: 0);
}
