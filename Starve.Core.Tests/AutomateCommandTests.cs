using GodotClient.Game;
using Google.Protobuf;
using Starve.Protocol;
using Starve.Proto.V1;

namespace Starve.Core.Tests;

public sealed class AutomateCommandTests
{
    [Theory]
    [InlineData(AutomateMode.Any)]
    [InlineData(AutomateMode.AttackOnly)]
    public void PlayerAutomateModeRoundTrips(AutomateMode mode)
    {
        var bytes = new PlayerAutomate { Mode = mode }.ToByteArray();

        Assert.Equal(mode, PlayerAutomate.Parser.ParseFrom(bytes).Mode);
    }

    [Fact]
    public void CommandServiceUsesSemanticAutomateModes()
    {
        var session = new RecordingSession();
        var commands = new CommandService(session);
        commands.BeginInputEpoch(7);

        var automate = commands.Automate();
        var attackNearest = commands.AttackNearest();

        Assert.Equal(2, session.Notifications.Count);
        Assert.All(session.Notifications, item => Assert.Equal(Routes.Automate, item.Route));
        Assert.Equal(AutomateMode.Any, PlayerAutomate.Parser.ParseFrom(session.Notifications[0].Data).Mode);
        Assert.Equal(
            AutomateMode.AttackOnly,
            PlayerAutomate.Parser.ParseFrom(session.Notifications[1].Data).Mode);
        Assert.Equal<ulong>(1, automate.Seq);
        Assert.Equal<ulong>(2, attackNearest.Seq);
        Assert.NotEqual<ulong>(0, automate.RequestId);
        Assert.True(attackNearest.RequestId > automate.RequestId);
    }

    [Fact]
    public void HeldInputTriggersImmediatelyRepeatsAndStopsOnRelease()
    {
        var state = new AutoActionInputState();
        var triggered = new List<AutoActionIntent>();

        state.Press(AutoActionIntent.AttackOnly, 0, triggered.Add);
        state.Tick(149, triggered.Add);
        state.Tick(150, triggered.Add);
        state.Release(AutoActionIntent.AttackOnly);
        state.Tick(300, triggered.Add);

        Assert.Equal(
            [AutoActionIntent.AttackOnly, AutoActionIntent.AttackOnly],
            triggered);
    }

    [Fact]
    public void SpaceAndAttackHeldStatesAreIndependent()
    {
        var state = new AutoActionInputState();
        var triggered = new List<AutoActionIntent>();
        state.Press(AutoActionIntent.Any, 0, triggered.Add);
        state.Press(AutoActionIntent.AttackOnly, 0, triggered.Add);
        state.Release(AutoActionIntent.Any);

        state.Tick(150, triggered.Add);

        Assert.Equal(
            [AutoActionIntent.Any, AutoActionIntent.AttackOnly, AutoActionIntent.AttackOnly],
            triggered);
        Assert.False(state.IsHeld(AutoActionIntent.Any));
        Assert.True(state.IsHeld(AutoActionIntent.AttackOnly));
    }

    private sealed class RecordingSession : ICommandSession
    {
        public List<(string Route, byte[] Data)> Notifications { get; } = [];

        public Task<byte[]?> RequestAsync(
            string route,
            byte[] data,
            int timeoutMs = 5000,
            CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(null);

        public void Notify(string route, byte[] data) => Notifications.Add((route, data));
    }
}
