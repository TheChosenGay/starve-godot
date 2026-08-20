using GodotClient.Game;
using Starve.Game.V1;

namespace Starve.Core.Tests;

public sealed class ImpactPresentationControllerTests
{
    [Fact]
    public void AuthoritativeHitPlaysOnceForDuplicateEvent()
    {
        var sink = new RecordingImpactSink();
        var controller = new ImpactPresentationController(sink);
        var (worldEvent, impact) = Impact(1, 10, CombatImpactResult.Hit);

        controller.Apply(worldEvent, impact);
        controller.Apply(worldEvent, impact);

        Assert.Equal([7UL], sink.Hits);
    }

    [Fact]
    public void SameActionTargetPlaysOnceAcrossDifferentEventIds()
    {
        var sink = new RecordingImpactSink();
        var controller = new ImpactPresentationController(sink);
        var first = Impact(1, 10, CombatImpactResult.Hit);
        var duplicate = Impact(2, 10, CombatImpactResult.Hit);

        controller.Apply(first.Event, first.Impact);
        controller.Apply(duplicate.Event, duplicate.Impact);

        Assert.Single(sink.Hits);
    }

    [Fact]
    public void PredictedHitConfirmationDoesNotReplay()
    {
        var sink = new RecordingImpactSink();
        var controller = new ImpactPresentationController(sink);
        controller.PredictHit(10, 7);
        var (worldEvent, impact) = Impact(1, 10, CombatImpactResult.Hit);

        controller.Apply(worldEvent, impact);

        Assert.Single(sink.Hits);
        Assert.Empty(sink.Corrections);
    }

    [Fact]
    public void PredictedMissCorrectsWithoutAnotherHit()
    {
        var sink = new RecordingImpactSink();
        var controller = new ImpactPresentationController(sink);
        controller.PredictHit(10, 7);
        var (worldEvent, impact) = Impact(1, 10, CombatImpactResult.Miss);

        controller.Apply(worldEvent, impact);

        Assert.Single(sink.Hits);
        Assert.Equal([(7UL, CombatImpactResult.Miss)], sink.Corrections);
        Assert.Equal([(7UL, CombatImpactResult.Miss)], sink.NonHits);
    }

    [Fact]
    public void BlockedDoesNotPlayOrInterruptHitVisual()
    {
        var sink = new RecordingImpactSink();
        var controller = new ImpactPresentationController(sink);
        var (worldEvent, impact) = Impact(1, 10, CombatImpactResult.Blocked);

        controller.Apply(worldEvent, impact);

        Assert.Empty(sink.Hits);
        Assert.Empty(sink.Corrections);
        Assert.Equal([(7UL, CombatImpactResult.Blocked)], sink.NonHits);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OutcomeAndImpactReorderingDoesNotDuplicate(bool outcomeFirst)
    {
        var actionSink = new RecordingActionSink();
        var impactSink = new RecordingImpactSink();
        var actions = new ActionPresentationController(actionSink, () => 0);
        var impacts = new ImpactPresentationController(impactSink);
        actions.Apply(3, new ActionState
        {
            ActionId = 10,
            Kind = ActionKind.Attack,
            Phase = ActionPhase.Recovery,
        });
        var outcome = new ActionOutcome
        {
            EntityId = 3,
            ActionId = 10,
            Kind = ActionKind.Attack,
            Result = ActionOutcomeResult.Completed,
            Tick = 5,
        };
        var (worldEvent, impact) = Impact(1, 10, CombatImpactResult.Hit);

        if (outcomeFirst)
        {
            actions.ApplyOutcome(outcome);
            impacts.Apply(worldEvent, impact);
        }
        else
        {
            impacts.Apply(worldEvent, impact);
            actions.ApplyOutcome(outcome);
        }
        actions.ApplyOutcome(outcome);
        impacts.Apply(worldEvent, impact);

        Assert.Single(actionSink.Finished);
        Assert.Single(impactSink.Hits);
    }

    [Fact]
    public void DamagedOutcomeReleasesActionBeforeHitPresentation()
    {
        var sink = new OrderedSink();
        var actions = new ActionPresentationController(sink, () => 0);
        var impacts = new ImpactPresentationController(sink);
        actions.Apply(7, new ActionState
        {
            ActionId = 10,
            Kind = ActionKind.Attack,
            Phase = ActionPhase.Windup,
        });
        var outcome = new ActionOutcome
        {
            EntityId = 7,
            ActionId = 10,
            Kind = ActionKind.Attack,
            Result = ActionOutcomeResult.Canceled,
            Reason = ActionOutcomeReason.Damaged,
            Tick = 5,
        };
        var (worldEvent, impact) = Impact(1, 10, CombatImpactResult.Hit);

        actions.ApplyOutcome(outcome);
        impacts.Apply(worldEvent, impact);

        Assert.Equal(["cancel", "hit"], sink.Order);
    }

    private static (WorldEvent Event, CombatImpactEvent Impact) Impact(
        ulong eventId,
        ulong actionId,
        CombatImpactResult result)
    {
        var impact = new CombatImpactEvent
        {
            SourceEntity = 3,
            TargetEntity = 7,
            SourceActionId = actionId,
            Result = result,
        };
        return (new WorldEvent { EventId = eventId, Tick = 5, Impact = impact }, impact);
    }

    private sealed class RecordingImpactSink : IImpactPresentationSink
    {
        public List<ulong> Hits { get; } = [];
        public List<(ulong Target, CombatImpactResult Result)> Corrections { get; } = [];
        public List<(ulong Target, CombatImpactResult Result)> NonHits { get; } = [];

        public void PlayHit(ulong targetEntity) => Hits.Add(targetEntity);
        public void CorrectPredictedHit(ulong targetEntity, CombatImpactResult result) =>
            Corrections.Add((targetEntity, result));
        public void PresentNonHit(ulong targetEntity, CombatImpactResult result) =>
            NonHits.Add((targetEntity, result));
    }

    private sealed class RecordingActionSink : IActionPresentationSink
    {
        public List<ulong> Finished { get; } = [];
        public void Apply(ulong entityId, ActionKind kind) { }
        public void Finish(ulong entityId) => Finished.Add(entityId);
        public void Cancel(ulong entityId) { }
        public void Death(ulong entityId) { }
    }

    private sealed class OrderedSink : IActionPresentationSink, IImpactPresentationSink
    {
        public List<string> Order { get; } = [];
        public void Apply(ulong entityId, ActionKind kind) { }
        public void Finish(ulong entityId) => Order.Add("finish");
        public void Cancel(ulong entityId) => Order.Add("cancel");
        public void Death(ulong entityId) => Order.Add("death");
        public void PlayHit(ulong targetEntity) => Order.Add("hit");
        public void CorrectPredictedHit(ulong targetEntity, CombatImpactResult result) { }
        public void PresentNonHit(ulong targetEntity, CombatImpactResult result) { }
    }
}
