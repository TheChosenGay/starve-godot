using Google.Protobuf;
using Starve.Game.V1;
using Starve.Protocol;
using Starve.Protocol.Pomelo;
using Starve.Protocol.World;

namespace Starve.Core.Tests;

public sealed class WorldInputContractTests
{
    [Fact]
    public void IgnoresOldEpochAndRegressingTick()
    {
        var world = new WorldService();
        world.HandleMessage(Push(
            Routes.Snapshot,
            new Snapshot { Tick = 10, InputEpoch = 5 }.ToByteArray()));
        var initialRevision = world.Revision;

        world.HandleMessage(Push(
            Routes.SnapshotDelta,
            new SnapshotDelta
            {
                Tick = 11,
                InputEpoch = 4,
                LastAcceptedSeq = 9,
            }.ToByteArray()));
        world.HandleMessage(Push(
            Routes.SnapshotDelta,
            new SnapshotDelta
            {
                Tick = 9,
                InputEpoch = 5,
                LastAcceptedSeq = 9,
            }.ToByteArray()));

        Assert.Equal(initialRevision, world.Revision);
        Assert.Equal(10, world.WorldTick);
        Assert.Equal<ulong>(0, world.LastAcceptedSeq);
    }

    [Fact]
    public void PublishesCurrentEpochAcknowledgement()
    {
        var world = new WorldService();
        (ulong Epoch, ulong Seq, long Tick) observed = default;
        world.InputAcknowledged += (epoch, seq, tick) => observed = (epoch, seq, tick);
        world.HandleMessage(Push(
            Routes.Snapshot,
            new Snapshot { Tick = 10, InputEpoch = 5 }.ToByteArray()));
        world.HandleMessage(Push(
            Routes.SnapshotDelta,
            new SnapshotDelta
            {
                Tick = 11,
                InputEpoch = 5,
                LastAcceptedSeq = 3,
            }.ToByteArray()));

        Assert.Equal((5UL, 3UL, 11L), observed);
        Assert.Equal(11, world.WorldTick);
        Assert.Equal<ulong>(3, world.LastAcceptedSeq);
    }

    private static PomeloMessage Push(string route, byte[] data) => new()
    {
        Type = MsgType.Push,
        Route = route,
        Data = data,
    };
}
