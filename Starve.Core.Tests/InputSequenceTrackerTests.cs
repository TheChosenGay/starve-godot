using Starve.Protocol;

namespace Starve.Core.Tests;

public sealed class InputSequenceTrackerTests
{
    [Fact]
    public void EpochResetRejectsOldAcknowledgement()
    {
        var tracker = new InputSequenceTracker();
        tracker.Begin(10);
        Assert.Equal<ulong>(1, tracker.Next());
        Assert.Equal<ulong>(2, tracker.Next());
        tracker.Acknowledge(10, 1);
        Assert.Equal<ulong>(1, tracker.Pending);

        tracker.Begin(11);
        tracker.Acknowledge(10, 2);
        Assert.Equal<ulong>(0, tracker.LastAccepted);
        Assert.Equal<ulong>(0, tracker.Pending);
    }

    [Fact]
    public void PredictionStopsWhenServerFallsTooFarBehind()
    {
        var tracker = new InputSequenceTracker();
        tracker.Begin(7);
        for (var i = 0; i < 5; i++) tracker.Next();

        Assert.False(tracker.CanPredict);
        tracker.Acknowledge(7, 2);
        Assert.True(tracker.CanPredict);
        Assert.Equal<ulong>(3, tracker.Pending);
    }

    [Fact]
    public void AcknowledgementCannotAdvanceBeyondSentInput()
    {
        var tracker = new InputSequenceTracker();
        tracker.Begin(3);
        tracker.Next();
        tracker.Acknowledge(3, 99);

        Assert.Equal<ulong>(1, tracker.LastAccepted);
        Assert.Equal<ulong>(0, tracker.Pending);
    }
}
