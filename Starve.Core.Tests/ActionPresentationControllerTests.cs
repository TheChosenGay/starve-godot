using GodotClient.Game;
using Starve.Game.V1;
using Starve.Protocol;

namespace Starve.Core.Tests;

public sealed class ActionPresentationControllerTests
{
    [Fact]
    public void SameKindAuthorityConfirmsPredictionWithoutRestart()
    {
        long now = 100;
        var sink = new RecordingSink();
        var controller = new ActionPresentationController(sink, () => now);

        controller.Predict(7, ActionKind.Attack, Ref());
        controller.Apply(7, State(11, ActionKind.Attack, ActionPhase.Windup));

        Assert.Equal(
            new ActionPresentationStatus(false, ActionKind.Attack, 11, ActionPhase.Windup, 3, 1, 13),
            controller.StatusOf(7));
        Assert.Single(sink.Applied);
        Assert.Empty(sink.Cleared);
    }

    [Fact]
    public void PredictionExpiresWithoutAuthority()
    {
        long now = 100;
        var sink = new RecordingSink();
        var controller = new ActionPresentationController(sink, () => now);
        controller.Predict(7, ActionKind.Chop, Ref());

        now = 599;
        controller.Tick();
        Assert.NotNull(controller.StatusOf(7));

        now = 600;
        controller.Tick();
        Assert.Null(controller.StatusOf(7));
        Assert.Equal([7UL], sink.Cleared);
    }

    [Fact]
    public void MovementCancelsPrediction()
    {
        var sink = new RecordingSink();
        var controller = new ActionPresentationController(sink, () => 0);
        controller.Predict(7, ActionKind.Mine, Ref());
        controller.CancelForMovement(7);
        Assert.Null(controller.StatusOf(7));
        Assert.Single(sink.Cleared);
    }

    [Fact]
    public void ExplicitCancelImmediatelyClearsSleepAndSuppressesResidualState()
    {
        var sink = new RecordingSink();
        var controller = new ActionPresentationController(sink, () => 0);
        var sleep = State(18, ActionKind.Sleep, ActionPhase.Windup);
        controller.Apply(7, sleep);

        controller.CancelLocally(7);
        controller.Apply(7, sleep);

        Assert.Single(sink.Applied);
        Assert.Single(sink.Cleared);
        Assert.Equal(ActionKind.Sleep, controller.StatusOf(7)!.Value.Kind);
    }

    [Fact]
    public void ExplicitCancelSuppressesLateAuthorityForPredictedSleep()
    {
        var sink = new RecordingSink();
        var controller = new ActionPresentationController(sink, () => 0);
        controller.Predict(7, ActionKind.Sleep, Ref());

        controller.CancelLocally(7);
        controller.Apply(7, State(18, ActionKind.Sleep, ActionPhase.Windup));

        Assert.Single(sink.Applied);
        Assert.Single(sink.Cleared);
        Assert.Equal(ActionKind.Sleep, controller.StatusOf(7)!.Value.Kind);
    }

    [Fact]
    public void PhaseUpdateDoesNotRestartAnimation()
    {
        var sink = new RecordingSink();
        var controller = new ActionPresentationController(sink, () => 0);
        controller.Apply(7, State(20, ActionKind.Attack, ActionPhase.Windup));
        controller.Apply(7, State(20, ActionKind.Attack, ActionPhase.Recovery));

        Assert.Single(sink.Applied);
        var status = controller.StatusOf(7);
        Assert.NotNull(status);
        Assert.Equal(ActionPhase.Recovery, status.Value.Phase);
    }

    [Fact]
    public void NewActionIdRestartsAnimation()
    {
        var sink = new RecordingSink();
        var controller = new ActionPresentationController(sink, () => 0);
        controller.Apply(7, State(20, ActionKind.Attack, ActionPhase.Windup));
        controller.Apply(7, State(21, ActionKind.Attack, ActionPhase.Windup));

        Assert.Equal(2, sink.Applied.Count);
        Assert.Equal(
            new ActionPresentationStatus(false, ActionKind.Attack, 21, ActionPhase.Windup, 0, 0, 13),
            controller.StatusOf(7));
    }

    [Fact]
    public void KindReplacementRestartsAnimation()
    {
        var sink = new RecordingSink();
        var controller = new ActionPresentationController(sink, () => 0);
        controller.Apply(7, State(20, ActionKind.Attack, ActionPhase.Windup));
        controller.Apply(7, State(20, ActionKind.Chop, ActionPhase.Windup));

        Assert.Equal(2, sink.Applied.Count);
    }

    [Fact]
    public void MovementSuppressesOldAuthorityUntilComponentIsAbsent()
    {
        var sink = new RecordingSink();
        var controller = new ActionPresentationController(sink, () => 0);
        controller.Apply(7, State(30, ActionKind.Attack, ActionPhase.Windup));

        controller.CancelForMovement(7);
        controller.Apply(7, State(30, ActionKind.Attack, ActionPhase.Windup));
        controller.Apply(7, State(30, ActionKind.Attack, ActionPhase.Recovery));

        Assert.Single(sink.Applied);
        Assert.Single(sink.Cleared);
        var status = controller.StatusOf(7);
        Assert.NotNull(status);
        Assert.Equal(ActionPhase.Recovery, status.Value.Phase);
    }

    [Fact]
    public void ComponentRemovalClearsSuppressionAndAllowsNewAction()
    {
        var sink = new RecordingSink();
        var controller = new ActionPresentationController(sink, () => 0);
        controller.Apply(7, State(30, ActionKind.Attack, ActionPhase.Windup));
        controller.CancelForMovement(7);

        controller.ObserveAbsent(7);
        controller.Apply(7, State(31, ActionKind.Attack, ActionPhase.Windup));

        Assert.Equal(2, sink.Applied.Count);
        Assert.Single(sink.Cleared);
        var status = controller.StatusOf(7);
        Assert.NotNull(status);
        Assert.Equal<ulong>(31, status.Value.ActionId);
    }

    [Fact]
    public void CanceledOutcomeClearsAuthoritativeVisual()
    {
        var sink = new RecordingSink();
        var controller = new ActionPresentationController(sink, () => 0);
        controller.Apply(7, State(40, ActionKind.Attack, ActionPhase.Windup));

        controller.ApplyOutcome(Outcome(40, ActionOutcomeResult.Canceled));

        Assert.Single(sink.Cleared);
    }

    [Fact]
    public void RejectedOutcomeClearsPrediction()
    {
        var sink = new RecordingSink();
        var controller = new ActionPresentationController(sink, () => 0);
        controller.Predict(7, ActionKind.Chop, Ref());

        controller.ApplyOutcome(Outcome(0, ActionOutcomeResult.Rejected));

        Assert.Null(controller.StatusOf(7));
        Assert.Single(sink.Cleared);
    }

    [Fact]
    public void DuplicateOutcomeIsIdempotentAndDoesNotAffectNewAction()
    {
        var sink = new RecordingSink();
        var controller = new ActionPresentationController(sink, () => 0);
        controller.Apply(7, State(40, ActionKind.Attack, ActionPhase.Windup));
        var canceled = Outcome(40, ActionOutcomeResult.Canceled);

        controller.ApplyOutcome(canceled);
        controller.ApplyOutcome(canceled);
        controller.Apply(7, State(41, ActionKind.Attack, ActionPhase.Windup));
        controller.ApplyOutcome(canceled);

        Assert.Equal(2, sink.Applied.Count);
        Assert.Single(sink.Cleared);
        var status = controller.StatusOf(7);
        Assert.NotNull(status);
        Assert.Equal<ulong>(41, status.Value.ActionId);
    }

    [Fact]
    public void CompletedOutcomeSuppressesResidualSnapshot()
    {
        var sink = new RecordingSink();
        var controller = new ActionPresentationController(sink, () => 0);
        var state = State(40, ActionKind.Attack, ActionPhase.Recovery);
        controller.Apply(7, state);

        controller.ApplyOutcome(Outcome(40, ActionOutcomeResult.Completed));
        controller.Apply(7, state);
        controller.ObserveAbsent(7);

        Assert.Single(sink.Applied);
        Assert.Single(sink.Finished);
        Assert.Empty(sink.Cleared);
    }

    [Theory]
    [InlineData(ActionOutcomeResult.Rejected)]
    [InlineData(ActionOutcomeResult.Canceled)]
    [InlineData(ActionOutcomeResult.Completed)]
    public void OldOutcomeDoesNotAffectNewPrediction(ActionOutcomeResult result)
    {
        var sink = new RecordingSink();
        var controller = new ActionPresentationController(sink, () => 0);
        controller.Predict(7, ActionKind.Attack, Ref(requestId: 100, seq: 1));
        controller.Predict(7, ActionKind.Chop, Ref(requestId: 101, seq: 2));

        controller.ApplyOutcome(Outcome(0, result, requestId: 100));

        var status = controller.StatusOf(7);
        Assert.NotNull(status);
        Assert.True(status.Value.Predicted);
        Assert.Equal(ActionKind.Chop, status.Value.Kind);
        Assert.Equal<ulong>(101, status.Value.RequestId);
        Assert.Empty(sink.Cleared);
        Assert.Empty(sink.Finished);
    }

    [Fact]
    public void OldActionStateDoesNotOverrideNewPrediction()
    {
        var sink = new RecordingSink();
        var controller = new ActionPresentationController(sink, () => 0);
        controller.Predict(7, ActionKind.Attack, Ref(requestId: 100, seq: 1));
        controller.Predict(7, ActionKind.Chop, Ref(requestId: 101, seq: 2));

        controller.Apply(7, State(20, ActionKind.Attack, ActionPhase.Windup, requestId: 100));

        var status = controller.StatusOf(7);
        Assert.NotNull(status);
        Assert.True(status.Value.Predicted);
        Assert.Equal(ActionKind.Chop, status.Value.Kind);
        Assert.Equal<ulong>(101, status.Value.RequestId);
        Assert.Equal(2, sink.Applied.Count);
    }

    [Fact]
    public void MatchingStateConfirmsNewestPrediction()
    {
        var sink = new RecordingSink();
        var controller = new ActionPresentationController(sink, () => 0);
        controller.Predict(7, ActionKind.Attack, Ref(requestId: 100, seq: 1));
        controller.Predict(7, ActionKind.Chop, Ref(requestId: 101, seq: 2));

        controller.Apply(7, State(21, ActionKind.Chop, ActionPhase.Windup, requestId: 101));

        Assert.Equal(
            new ActionPresentationStatus(false, ActionKind.Chop, 21, ActionPhase.Windup, 3, 2, 101),
            controller.StatusOf(7));
        Assert.Equal(2, sink.Applied.Count);
    }

    [Fact]
    public void HigherRequestStateReplacesPrediction()
    {
        var sink = new RecordingSink();
        var controller = new ActionPresentationController(sink, () => 0);
        controller.Predict(7, ActionKind.Attack, Ref(requestId: 100));

        controller.Apply(7, State(21, ActionKind.Chop, ActionPhase.Windup, requestId: 101));

        var status = controller.StatusOf(7);
        Assert.NotNull(status);
        Assert.False(status.Value.Predicted);
        Assert.Equal(ActionKind.Chop, status.Value.Kind);
        Assert.Equal<ulong>(101, status.Value.RequestId);
    }

    [Fact]
    public void OutcomeCanMatchAuthoritativeEntryByRequestId()
    {
        var sink = new RecordingSink();
        var controller = new ActionPresentationController(sink, () => 0);
        controller.Apply(7, State(21, ActionKind.Chop, ActionPhase.Windup, requestId: 101));

        controller.ApplyOutcome(Outcome(999, ActionOutcomeResult.Canceled, requestId: 101));

        Assert.Single(sink.Cleared);
    }

    [Fact]
    public void OldCraftResponseCannotCancelNewPrediction()
    {
        var sink = new RecordingSink();
        var controller = new ActionPresentationController(sink, () => 0);
        controller.Predict(7, ActionKind.Craft, Ref(requestId: 100, seq: 1));
        controller.Predict(7, ActionKind.Attack, Ref(requestId: 101, seq: 2));

        controller.CancelPrediction(7, requestId: 100);

        Assert.Equal<ulong>(101, controller.StatusOf(7)!.Value.RequestId);
        Assert.Empty(sink.Cleared);
    }

    [Fact]
    public void HauntPredictionBecomesUninterruptibleAuthority()
    {
        var sink = new RecordingSink();
        var controller = new ActionPresentationController(sink, () => 0);
        controller.Predict(7, ActionKind.Haunt, Ref());

        controller.CancelForMovement(7);
        controller.CancelLocally(7);
        controller.Apply(7, State(50, ActionKind.Haunt, ActionPhase.Windup, uninterruptible: true));

        var status = controller.StatusOf(7);
        Assert.NotNull(status);
        Assert.False(status.Value.Predicted);
        Assert.True(status.Value.Uninterruptible);
        Assert.Empty(sink.Cleared);
    }

    [Theory]
    [InlineData(ActionOutcomeResult.Rejected)]
    [InlineData(ActionOutcomeResult.Canceled)]
    public void RejectedOrCanceledHauntUnlocksAndSuppressesResidualState(ActionOutcomeResult result)
    {
        var sink = new RecordingSink();
        var controller = new ActionPresentationController(sink, () => 0);
        var state = State(50, ActionKind.Haunt, ActionPhase.Windup, uninterruptible: true);
        controller.Apply(7, state);

        controller.ApplyOutcome(Outcome(50, result, kind: ActionKind.Haunt));
        controller.Apply(7, state);

        Assert.Null(controller.StatusOf(7));
        Assert.Single(sink.Cleared);
    }

    [Fact]
    public void CompletedHauntWaitsForAuthoritativeComponentRemoval()
    {
        var sink = new RecordingSink();
        var controller = new ActionPresentationController(sink, () => 0);
        controller.Apply(7, State(50, ActionKind.Haunt, ActionPhase.Recovery, uninterruptible: true));

        controller.ApplyOutcome(Outcome(50, ActionOutcomeResult.Completed, kind: ActionKind.Haunt));
        Assert.NotNull(controller.StatusOf(7));

        controller.ObserveAbsent(7);
        Assert.Null(controller.StatusOf(7));
        Assert.Single(sink.Finished);
    }

    private static InputCommandRef Ref(ulong requestId = 13, ulong seq = 1) => new(3, seq, requestId);

    private static ActionState State(
        ulong id,
        ActionKind kind,
        ActionPhase phase,
        ulong requestId = 13,
        bool uninterruptible = false) => new()
    {
        ActionId = id,
        Kind = kind,
        Phase = phase,
        RequestId = requestId,
        Uninterruptible = uninterruptible,
    };

    private static ActionOutcome Outcome(
        ulong id,
        ActionOutcomeResult result,
        ulong requestId = 13,
        ActionKind kind = ActionKind.Attack) => new()
    {
        EntityId = 7,
        ActionId = id,
        RequestId = requestId,
        Kind = kind,
        Result = result,
        Tick = 20,
    };

    private sealed class RecordingSink : IActionPresentationSink
    {
        public List<(ulong EntityId, ActionKind Kind)> Applied { get; } = [];
        public List<ulong> Cleared { get; } = [];
        public List<ulong> Finished { get; } = [];

        public void Apply(ulong entityId, ActionKind kind) => Applied.Add((entityId, kind));
        public void Finish(ulong entityId) => Finished.Add(entityId);
        public void Cancel(ulong entityId) => Cleared.Add(entityId);
        public void Death(ulong entityId) => Cleared.Add(entityId);
    }
}
