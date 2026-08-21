using Google.Protobuf;
using Starve.Protocol;
using Starve.Proto.V1;

namespace Starve.Core.Tests;

public sealed class CommandServiceTests
{
    [Fact]
    public void ControlCommandsShareSequenceAndWriteStableIdentity()
    {
        var session = new RecordingSession();
        var commands = new CommandService(session);
        commands.BeginInputEpoch(42);

        var move = commands.Move(1, -1);
        commands.Pickup(90);
        var gather = commands.Gather(91);
        var attack = commands.Attack(92);
        var chop = commands.Chop(93);
        var mine = commands.Mine(94);
        var automate = commands.Automate();
        var attackNearest = commands.AttackNearest();
        var sleep = commands.Sleep();
        var cancelSleep = commands.CancelSleep();
        var craft = commands.BeginCraft("axe");
        var cancel = commands.CancelCraft();

        Assert.Equal(Enumerable.Range(1, 11).Select(i => (ulong)i),
            new[]
            {
                move.Seq,
                gather.Seq,
                attack.Seq,
                chop.Seq,
                mine.Seq,
                automate.Seq,
                attackNearest.Seq,
                sleep.Seq,
                cancelSleep.Seq,
                craft.CommandRef.Seq,
                cancel.Seq,
            });
        Assert.All(
            new[]
            {
                move, gather, attack, chop, mine, automate, attackNearest,
                sleep, cancelSleep, craft.CommandRef, cancel,
            },
            command => Assert.Equal<ulong>(42, command.InputEpoch));
        Assert.Equal<ulong>(0, move.RequestId);
        Assert.Equal<ulong>(0, cancel.RequestId);

        var requestIds = new[]
        {
            gather.RequestId,
            attack.RequestId,
            chop.RequestId,
            mine.RequestId,
            automate.RequestId,
            attackNearest.RequestId,
            sleep.RequestId,
            cancelSleep.RequestId,
            craft.CommandRef.RequestId,
        };
        Assert.All(requestIds, requestId => Assert.NotEqual<ulong>(0, requestId));
        Assert.True(requestIds.Zip(requestIds.Skip(1), (left, right) => right > left).All(x => x));

        AssertMove(session.Notification(Routes.Move), move);
        AssertGather(session.Notification(Routes.Gather), gather);
        AssertAttack(session.Notification(Routes.Attack), attack);
        AssertChop(session.Notification(Routes.Chop), chop);
        AssertMine(session.Notification(Routes.Mine), mine);
        AssertAutomate(session.Notifications.Where(x => x.Route == Routes.Automate).ElementAt(0), automate);
        AssertAutomate(session.Notifications.Where(x => x.Route == Routes.Automate).ElementAt(1), attackNearest);
        AssertSleep(session.Notification(Routes.Sleep), sleep);
        AssertSleep(session.Notification(Routes.CancelSleep), cancelSleep);
        AssertCancel(session.Notification(Routes.CancelCraft), cancel);

        var craftMessage = PlayerCraft.Parser.ParseFrom(Assert.Single(session.Requests).Data);
        Assert.Equal("axe", craftMessage.RecipeId);
        AssertIdentity(craftMessage.Seq, craftMessage.InputEpoch, craftMessage.RequestId, craft.CommandRef);
        Assert.Equal<ulong>(11, commands.LastSentSeq);
    }

    [Fact]
    public void RequestIdDoesNotResetAcrossInputEpochs()
    {
        var commands = new CommandService(new RecordingSession());
        commands.BeginInputEpoch(10);
        var before = commands.Attack(1);

        commands.BeginInputEpoch(11);
        var after = commands.Gather(2);

        Assert.Equal<ulong>(1, before.Seq);
        Assert.Equal<ulong>(1, after.Seq);
        Assert.True(after.RequestId > before.RequestId);
    }

    [Fact]
    public async Task CraftResultKeepsPreallocatedCommandIdentity()
    {
        var response = new CraftResponse { Started = true, Ticks = 20 };
        var session = new RecordingSession(response.ToByteArray());
        var commands = new CommandService(session);
        commands.BeginInputEpoch(5);

        var result = await commands.CraftAsync("axe");

        Assert.Same(response.GetType(), result.Response!.GetType());
        Assert.True(result.Response.Started);
        Assert.Equal<ulong>(5, result.CommandRef.InputEpoch);
        Assert.Equal<ulong>(1, result.CommandRef.Seq);
        Assert.NotEqual<ulong>(0, result.CommandRef.RequestId);
        var sent = PlayerCraft.Parser.ParseFrom(Assert.Single(session.Requests).Data);
        AssertIdentity(sent.Seq, sent.InputEpoch, sent.RequestId, result.CommandRef);
    }

    private static void AssertMove((string Route, byte[] Data) sent, InputCommandRef command)
    {
        var message = PlayerMove.Parser.ParseFrom(sent.Data);
        Assert.Equal(1, message.Dx);
        Assert.Equal(-1, message.Dy);
        AssertIdentity(message.Seq, message.InputEpoch, 0, command);
    }

    private static void AssertGather((string Route, byte[] Data) sent, InputCommandRef command)
    {
        var message = PlayerGather.Parser.ParseFrom(sent.Data);
        Assert.Equal<ulong>(91, message.TargetEntity);
        AssertIdentity(message.Seq, message.InputEpoch, message.RequestId, command);
    }

    private static void AssertAttack((string Route, byte[] Data) sent, InputCommandRef command)
    {
        var message = PlayerAttack.Parser.ParseFrom(sent.Data);
        Assert.Equal<ulong>(92, message.TargetEntity);
        AssertIdentity(message.Seq, message.InputEpoch, message.RequestId, command);
    }

    private static void AssertChop((string Route, byte[] Data) sent, InputCommandRef command)
    {
        var message = PlayerChop.Parser.ParseFrom(sent.Data);
        Assert.Equal<ulong>(93, message.TargetEntity);
        AssertIdentity(message.Seq, message.InputEpoch, message.RequestId, command);
    }

    private static void AssertMine((string Route, byte[] Data) sent, InputCommandRef command)
    {
        var message = PlayerMine.Parser.ParseFrom(sent.Data);
        Assert.Equal<ulong>(94, message.TargetEntity);
        AssertIdentity(message.Seq, message.InputEpoch, message.RequestId, command);
    }

    private static void AssertAutomate((string Route, byte[] Data) sent, InputCommandRef command)
    {
        var message = PlayerAutomate.Parser.ParseFrom(sent.Data);
        AssertIdentity(message.Seq, message.InputEpoch, message.RequestId, command);
    }

    private static void AssertSleep((string Route, byte[] Data) sent, InputCommandRef command)
    {
        var message = PlayerSleep.Parser.ParseFrom(sent.Data);
        AssertIdentity(message.Seq, message.InputEpoch, message.RequestId, command);
    }

    private static void AssertCancel((string Route, byte[] Data) sent, InputCommandRef command)
    {
        var message = PlayerCancelCraft.Parser.ParseFrom(sent.Data);
        AssertIdentity(message.Seq, message.InputEpoch, 0, command);
    }

    private static void AssertIdentity(
        ulong seq,
        ulong inputEpoch,
        ulong requestId,
        InputCommandRef command)
    {
        Assert.Equal(command.Seq, seq);
        Assert.Equal(command.InputEpoch, inputEpoch);
        Assert.Equal(command.RequestId, requestId);
    }

    private sealed class RecordingSession(byte[]? response = null) : ICommandSession
    {
        public List<(string Route, byte[] Data)> Notifications { get; } = [];
        public List<(string Route, byte[] Data)> Requests { get; } = [];

        public Task<byte[]?> RequestAsync(
            string route,
            byte[] data,
            int timeoutMs = 5000,
            CancellationToken ct = default)
        {
            Requests.Add((route, data));
            return Task.FromResult(response);
        }

        public void Notify(string route, byte[] data) => Notifications.Add((route, data));

        public (string Route, byte[] Data) Notification(string route) =>
            Assert.Single(Notifications, item => item.Route == route);
    }
}
