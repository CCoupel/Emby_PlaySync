using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Engine;
using Xunit;

namespace EmbySharedPlaylist.Tests;

public class PlaybackEventProcessorDevTests
{
    private sealed class Rig
    {
        public readonly PlayedTransitionTracker Tracker = new();
        public readonly HandlerStats Stats = new();
        public readonly List<(string User, string Item)> Handled = new();
        public bool Suspended;
        public bool Scope;
        public Exception? Throw;
        public readonly PlaybackEventProcessor Processor;

        public Rig()
        {
            Processor = new PlaybackEventProcessor(Tracker, (u, i) =>
            {
                Handled.Add((u, i));
                if (Throw != null) throw Throw;
                return new RemovalResult(1, 1, 1, 0, 0);
            }, Stats, () => Suspended, () => Scope);
        }
    }

    [Fact]
    public void Transition_IsHandledOnce_AndTheDurationIsRecorded()
    {
        var r = new Rig();
        Assert.Equal(PlaybackEventOutcome.Handled, r.Processor.Process("u", "i", "TogglePlayed", true));
        Assert.Equal(new[] { ("u", "i") }, r.Handled);
        Assert.Equal(1, r.Stats.Snapshot().Count);
    }

    [Theory]
    [InlineData("PlaybackProgress", false)]
    [InlineData("PlaybackStart", true)]
    [InlineData("PlaybackFinished", false)]
    public void NoTransition_ReturnsImmediately_NoEngineNoStats(string reason, bool played)
    {
        var r = new Rig();
        Assert.Equal(PlaybackEventOutcome.NoTransition, r.Processor.Process("u", "i", reason, played));
        Assert.Empty(r.Handled);
        Assert.Equal(0, r.Stats.Snapshot().Count);
    }

    [Fact]
    public void Suspended_TouchesNothing_NotEvenTheTrackerMemory()
    {
        var r = new Rig();
        r.Suspended = true;
        Assert.Equal(PlaybackEventOutcome.Suspended, r.Processor.Process("u", "i", "PlaybackFinished", true));
        Assert.Empty(r.Handled);
        Assert.Equal(0, r.Tracker.Count);
        r.Suspended = false;
        Assert.Equal(PlaybackEventOutcome.Handled, r.Processor.Process("u", "i", "PlaybackFinished", true)); // rien n'a été mémorisé
    }

    [Fact]
    public void WriteScope_IgnoresOurOwnEvents_NotEvenTheTrackerMemory()
    {
        var r = new Rig();
        r.Scope = true;
        Assert.Equal(PlaybackEventOutcome.WriteScope, r.Processor.Process("u", "i", "TogglePlayed", true));
        Assert.Empty(r.Handled);
        Assert.Equal(0, r.Tracker.Count);
    }

    [Fact]
    public void SuspensionIsCheckedBeforeTheWriteScope() // ordre des gardes
    {
        var r = new Rig();
        r.Suspended = true; r.Scope = true;
        Assert.Equal(PlaybackEventOutcome.Suspended, r.Processor.Process("u", "i", "TogglePlayed", true));
    }

    [Fact]
    public void EngineException_Propagates_ButTheDurationIsStillRecorded()
    {
        var r = new Rig();
        r.Throw = new InvalidOperationException();
        Assert.Throws<InvalidOperationException>(() => r.Processor.Process("u", "i", "TogglePlayed", true));
        Assert.Equal(1, r.Stats.Snapshot().Count);
    }

    [Fact]
    public void FullScenario_FinishThenReplayAlreadyPlayed_HandlesOnlyTheFirst()
    {
        var r = new Rig();
        r.Processor.Process("u", "i", "PlaybackStart", false);
        r.Processor.Process("u", "i", "PlaybackProgress", false);
        Assert.Equal(PlaybackEventOutcome.Handled, r.Processor.Process("u", "i", "PlaybackFinished", true));
        r.Processor.Process("u", "i", "PlaybackStart", true);                    // relecture d'un média déjà lu
        Assert.Equal(PlaybackEventOutcome.NoTransition, r.Processor.Process("u", "i", "PlaybackFinished", true));
        Assert.Single(r.Handled);
    }

    [Fact]
    public void DefaultWriteScopeGuard_UsesTheRealWriteScope()
    {
        var processor = new PlaybackEventProcessor(new PlayedTransitionTracker(), (u, i) => null, new HandlerStats(), () => false);
        using (WriteScope.Enter()) Assert.Equal(PlaybackEventOutcome.WriteScope, processor.Process("u", "i", "TogglePlayed", true));
        Assert.Equal(PlaybackEventOutcome.Handled, processor.Process("u", "i", "TogglePlayed", true));
    }
}
