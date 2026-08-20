using Google.Protobuf;
using Starve.Game.V1;
using Starve.Protocol;
using Starve.Protocol.Pomelo;
using Starve.Protocol.World;

namespace Starve.Core.Tests;

public sealed class WorldEventContractTests
{
    [Fact]
    public void DeltaAppliesComponentsAndRemovalsBeforePublishingEvents()
    {
        var world = ReadyWorld();
        var observed = 0;
        world.CombatImpactReceived += (_, _) =>
        {
            observed++;
            var entity = AssertEntity(world, 7);
            Assert.Equal(8, entity.Get("Health", Health.Parser)?.Cur);
            Assert.False(entity.Components.ContainsKey("ActionState"));
        };
        var delta = DeltaWithImpact(eventId: 100);

        world.HandleMessage(Push(Routes.SnapshotDelta, delta));
        world.HandleMessage(Push(Routes.SnapshotDelta, delta));

        Assert.Equal(1, observed);
    }

    [Fact]
    public void NewFullSnapshotResetsEventIdScope()
    {
        var world = ReadyWorld();
        var observed = 0;
        world.WorldEventReceived += _ => observed++;
        world.HandleMessage(Push(Routes.SnapshotDelta, DeltaWithImpact(100)));

        world.HandleMessage(Push(Routes.Snapshot, BaseSnapshot(tick: 20)));
        world.HandleMessage(Push(Routes.SnapshotDelta, DeltaWithImpact(100, tick: 21)));

        Assert.Equal(2, observed);
    }

    [Fact]
    public void PoisonAndStarvationHealthEventsNeverPublishCombatImpact()
    {
        var world = ReadyWorld();
        var impacts = 0;
        var healthChanges = 0;
        world.CombatImpactReceived += (_, _) => impacts++;
        world.HealthChangedReceived += (_, _) => healthChanges++;
        var delta = new SnapshotDelta
        {
            Tick = 11,
            InputEpoch = 1,
            Events =
            {
                HealthEvent(201, HealthChangeCause.Poison),
                HealthEvent(202, HealthChangeCause.Starvation),
            },
        };

        world.HandleMessage(Push(Routes.SnapshotDelta, delta));

        Assert.Equal(0, impacts);
        Assert.Equal(2, healthChanges);
    }

    [Fact]
    public void DamagedOutcomePublishesBeforeHitInEventOrder()
    {
        var world = ReadyWorld();
        var order = new List<string>();
        world.ActionOutcomeReceived += _ => order.Add("outcome");
        world.CombatImpactReceived += (_, _) => order.Add("impact");
        world.HealthChangedReceived += (_, _) => order.Add("health");
        var outcome = DamagedOutcome();
        var delta = new SnapshotDelta
        {
            Tick = 11,
            InputEpoch = 1,
            RemovedComponents =
            {
                new RemovedComponent { EntityId = 7, Components = { "ActionState" } },
            },
            Events =
            {
                new WorldEvent { EventId = 301, Tick = 11, Outcome = outcome },
                new WorldEvent
                {
                    EventId = 302,
                    Tick = 11,
                    Impact = new CombatImpactEvent
                    {
                        SourceEntity = 3,
                        TargetEntity = 7,
                        SourceActionId = 50,
                        Result = CombatImpactResult.Hit,
                    },
                },
                new WorldEvent
                {
                    EventId = 303,
                    Tick = 11,
                    HealthChanged = new HealthChangedEvent
                    {
                        TargetEntity = 7,
                        SourceEntity = 3,
                        SourceActionId = 50,
                        Delta = -2,
                        Cause = HealthChangeCause.Attack,
                    },
                },
            },
        };

        world.HandleMessage(Push(Routes.SnapshotDelta, delta));

        Assert.Equal(["outcome", "impact", "health"], order);
        Assert.False(AssertEntity(world, 7).Components.ContainsKey("ActionState"));
    }

    [Fact]
    public void EmbeddedAndLegacyOutcomeShareIdempotencyKey()
    {
        var world = ReadyWorld();
        var observed = 0;
        world.ActionOutcomeReceived += _ => observed++;
        var outcome = DamagedOutcome();
        world.HandleMessage(Push(
            Routes.SnapshotDelta,
            new SnapshotDelta
            {
                Tick = 11,
                InputEpoch = 1,
                Events = { new WorldEvent { EventId = 401, Tick = 11, Outcome = outcome } },
            }));

        world.HandleMessage(Push(Routes.ActionOutcome, outcome));

        Assert.Equal(1, observed);
    }

    private static WorldService ReadyWorld()
    {
        var world = new WorldService();
        world.HandleMessage(Push(Routes.Snapshot, BaseSnapshot(10)));
        return world;
    }

    private static Snapshot BaseSnapshot(ulong tick) => new()
    {
        Tick = tick,
        InputEpoch = 1,
        Entities =
        {
            new EntityState
            {
                EntityId = 7,
                Components =
                {
                    Component("Health", new Health { Cur = 10, Max = 10 }),
                    Component("ActionState", new ActionState
                    {
                        ActionId = 50,
                        Kind = ActionKind.Attack,
                        Phase = ActionPhase.Windup,
                    }),
                },
            },
        },
    };

    private static SnapshotDelta DeltaWithImpact(ulong eventId, ulong tick = 11) => new()
    {
        Tick = tick,
        InputEpoch = 1,
        Entities =
        {
            new EntityState
            {
                EntityId = 7,
                Components = { Component("Health", new Health { Cur = 8, Max = 10 }) },
            },
        },
        RemovedComponents =
        {
            new RemovedComponent { EntityId = 7, Components = { "ActionState" } },
        },
        Events =
        {
            new WorldEvent
            {
                EventId = eventId,
                Tick = (long)tick,
                Impact = new CombatImpactEvent
                {
                    SourceEntity = 3,
                    TargetEntity = 7,
                    SourceActionId = 50,
                    Result = CombatImpactResult.Hit,
                },
            },
        },
    };

    private static WorldEvent HealthEvent(ulong eventId, HealthChangeCause cause) => new()
    {
        EventId = eventId,
        Tick = 11,
        HealthChanged = new HealthChangedEvent
        {
            TargetEntity = 7,
            Delta = -1,
            Cause = cause,
        },
    };

    private static ActionOutcome DamagedOutcome() => new()
    {
        EntityId = 7,
        ActionId = 50,
        RequestId = 9,
        Kind = ActionKind.Attack,
        Result = ActionOutcomeResult.Canceled,
        Reason = ActionOutcomeReason.Damaged,
        Tick = 11,
    };

    private static ComponentState Component(string name, IMessage payload) => new()
    {
        Component = name,
        Data = ByteString.CopyFrom(payload.ToByteArray()),
    };

    private static EntityView AssertEntity(WorldService world, ulong id)
    {
        Assert.True(world.Entities.TryGetValue(id, out var entity));
        return entity!;
    }

    private static PomeloMessage Push(string route, IMessage payload) => new()
    {
        Type = MsgType.Push,
        Route = route,
        Data = payload.ToByteArray(),
    };
}
