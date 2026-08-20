using Google.Protobuf;
using Starve.Game.V1;
using Starve.Protocol;
using Starve.Protocol.Pomelo;
using Starve.Protocol.World;

namespace Starve.Core.Tests;

public sealed class WorldActionStateTests
{
    [Fact]
    public void ParsesTypedActionOutcomePush()
    {
        var world = new WorldService();
        ActionOutcome? observed = null;
        world.ActionOutcomeReceived += outcome => observed = outcome;

        world.HandleMessage(Push(
            Routes.ActionOutcome,
            new ActionOutcome
            {
                EntityId = 7,
                ActionId = 40,
                Kind = ActionKind.Attack,
                Result = ActionOutcomeResult.Canceled,
                Reason = ActionOutcomeReason.Moved,
                Tick = 12,
            }.ToByteArray()));

        Assert.NotNull(observed);
        Assert.Equal<ulong>(40, observed.ActionId);
        Assert.Equal(ActionOutcomeReason.Moved, observed.Reason);
    }

    [Fact]
    public void IncrementalActionStateMergesWithoutReplacingOtherComponents()
    {
        var world = new WorldService();
        world.HandleMessage(Push(
            Routes.Snapshot,
            new Snapshot
            {
                Tick = 10,
                InputEpoch = 3,
                Entities =
                {
                    Entity(7,
                        Component("Health", new Health { Cur = 9, Max = 10 }),
                        Component("ActionState", Action(40, ActionPhase.Windup))),
                },
            }.ToByteArray()));

        world.HandleMessage(Push(
            Routes.SnapshotDelta,
            new SnapshotDelta
            {
                Tick = 11,
                InputEpoch = 3,
                Entities =
                {
                    Entity(7, Component("ActionState", Action(40, ActionPhase.Recovery))),
                },
            }.ToByteArray()));

        var view = AssertEntity(world, 7);
        Assert.Equal(9, view.Get("Health", Health.Parser)?.Cur);
        var action = view.Get("ActionState", ActionState.Parser);
        Assert.NotNull(action);
        Assert.Equal<ulong>(40, action.ActionId);
        Assert.Equal(ActionPhase.Recovery, action.Phase);
    }

    [Fact]
    public void RemovedComponentImmediatelyRemovesActionState()
    {
        var world = new WorldService();
        world.HandleMessage(Push(
            Routes.Snapshot,
            new Snapshot
            {
                Tick = 10,
                InputEpoch = 3,
                Entities = { Entity(7, Component("ActionState", Action(40, ActionPhase.Windup))) },
            }.ToByteArray()));

        world.HandleMessage(Push(
            Routes.SnapshotDelta,
            new SnapshotDelta
            {
                Tick = 11,
                InputEpoch = 3,
                RemovedComponents =
                {
                    new RemovedComponent { EntityId = 7, Components = { "ActionState" } },
                },
            }.ToByteArray()));

        Assert.False(AssertEntity(world, 7).Components.ContainsKey("ActionState"));
    }

    private static ActionState Action(ulong id, ActionPhase phase) => new()
    {
        ActionId = id,
        Kind = ActionKind.Attack,
        Phase = phase,
    };

    private static EntityState Entity(ulong id, params ComponentState[] components) => new()
    {
        EntityId = id,
        Components = { components },
    };

    private static ComponentState Component(string name, IMessage message) => new()
    {
        Component = name,
        Data = ByteString.CopyFrom(message.ToByteArray()),
    };

    private static EntityView AssertEntity(WorldService world, ulong id)
    {
        Assert.True(world.Entities.TryGetValue(id, out var view));
        return view!;
    }

    private static PomeloMessage Push(string route, byte[] data) => new()
    {
        Type = MsgType.Push,
        Route = route,
        Data = data,
    };
}
