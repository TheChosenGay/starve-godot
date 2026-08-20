using GodotClient.Game;
using Google.Protobuf;
using Starve.Game.V1;
using Starve.Protocol;
using Starve.Protocol.Pomelo;
using Starve.Protocol.World;

namespace Starve.Core.Tests;

/// <summary>纯确定性的动作网络丢包、延迟与重排契约；不使用真实网络或时间等待。</summary>
public sealed class ActionNetworkFaultTests
{
    [Fact]
    public void LostStartStateButOutcomeArrivesClearsPrediction()
    {
        var harness = new Harness();
        harness.Controller.Predict(Harness.EntityId, ActionKind.Attack);

        // ActionState start 丢失，只收到生命周期结果。
        harness.ReceiveOutcome(10, ActionOutcomeResult.Completed);

        Assert.Null(harness.Controller.StatusOf(Harness.EntityId));
        Assert.Single(harness.Sink.Finished);
    }

    [Fact]
    public void LostOutcomeButRemovedComponentClearsAuthority()
    {
        var harness = new Harness();
        harness.ReceiveFullState(10);
        harness.SyncSnapshotState();

        // Outcome 丢失，仅收到增量里的 removed component。
        harness.ReceiveRemovedComponent();
        harness.SyncSnapshotState();

        Assert.Null(harness.Controller.StatusOf(Harness.EntityId));
        Assert.Single(harness.Sink.Cleared);
    }

    [Fact]
    public void OutcomeBeforeOldStartStateDoesNotReplay()
    {
        var harness = new Harness();
        harness.Controller.Predict(Harness.EntityId, ActionKind.Attack);

        // 重排：结果先到，旧 start 快照后到。
        harness.ReceiveOutcome(10, ActionOutcomeResult.Completed);
        harness.ReceiveFullState(10);
        harness.SyncSnapshotState();

        Assert.Single(harness.Sink.Applied);
        Assert.Single(harness.Sink.Finished);
    }

    [Fact]
    public void LostStartStateTimesOutAfterDeterministicLatency()
    {
        var harness = new Harness();
        harness.Controller.Predict(Harness.EntityId, ActionKind.Chop);

        harness.NowMs = 499;
        harness.Controller.Tick();
        Assert.NotNull(harness.Controller.StatusOf(Harness.EntityId));

        harness.NowMs = 501;
        harness.Controller.Tick();
        Assert.Null(harness.Controller.StatusOf(Harness.EntityId));
        Assert.Single(harness.Sink.Cleared);
    }

    [Fact]
    public void NewActionIdPlaysAfterOldOutcomeAndResidualSnapshot()
    {
        var harness = new Harness();

        harness.ReceiveOutcome(10, ActionOutcomeResult.Completed);
        harness.ReceiveFullState(10);
        harness.SyncSnapshotState();
        Assert.Empty(harness.Sink.Applied);

        harness.ReceiveDeltaState(11);
        harness.SyncSnapshotState();

        Assert.Single(harness.Sink.Applied);
        var status = harness.Controller.StatusOf(Harness.EntityId);
        Assert.NotNull(status);
        Assert.Equal<ulong>(11, status.Value.ActionId);
    }

    private sealed class Harness
    {
        public const ulong EntityId = 7;

        public long NowMs { get; set; }
        public RecordingSink Sink { get; } = new();
        public ActionPresentationController Controller { get; }
        private WorldService World { get; } = new();

        public Harness()
        {
            Controller = new ActionPresentationController(Sink, () => NowMs);
            World.ActionOutcomeReceived += Controller.ApplyOutcome;
        }

        public void ReceiveOutcome(ulong actionId, ActionOutcomeResult result) =>
            World.HandleMessage(Push(
                Routes.ActionOutcome,
                new ActionOutcome
                {
                    EntityId = EntityId,
                    ActionId = actionId,
                    RequestId = 20,
                    Kind = ActionKind.Attack,
                    Result = result,
                    Tick = 30,
                }));

        public void ReceiveFullState(ulong actionId) =>
            World.HandleMessage(Push(
                Routes.Snapshot,
                new Snapshot
                {
                    Tick = 10,
                    InputEpoch = 1,
                    Entities = { EntityWithAction(actionId) },
                }));

        public void ReceiveDeltaState(ulong actionId) =>
            World.HandleMessage(Push(
                Routes.SnapshotDelta,
                new SnapshotDelta
                {
                    Tick = 12,
                    InputEpoch = 1,
                    Entities = { EntityWithAction(actionId) },
                }));

        public void ReceiveRemovedComponent() =>
            World.HandleMessage(Push(
                Routes.SnapshotDelta,
                new SnapshotDelta
                {
                    Tick = 11,
                    InputEpoch = 1,
                    RemovedComponents =
                    {
                        new RemovedComponent
                        {
                            EntityId = EntityId,
                            Components = { "ActionState" },
                        },
                    },
                }));

        public void SyncSnapshotState()
        {
            var state = World.Entities.TryGetValue(EntityId, out var view)
                ? view.Get("ActionState", ActionState.Parser)
                : null;
            if (state is null)
            {
                Controller.ObserveAbsent(EntityId);
            }
            else
            {
                Controller.Apply(EntityId, state);
            }
        }

        private static EntityState EntityWithAction(ulong actionId) => new()
        {
            EntityId = EntityId,
            Components =
            {
                new ComponentState
                {
                    Component = "ActionState",
                    Data = ByteString.CopyFrom(new ActionState
                    {
                        ActionId = actionId,
                        Kind = ActionKind.Attack,
                        Phase = ActionPhase.Windup,
                    }.ToByteArray()),
                },
            },
        };

        private static PomeloMessage Push(string route, IMessage payload) => new()
        {
            Type = MsgType.Push,
            Route = route,
            Data = payload.ToByteArray(),
        };
    }

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
