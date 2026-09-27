using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Engine;
using Xunit;

namespace EmbySharedPlaylist.Tests;

public class PlaybackEventProcessorDevTests
{
    private sealed class Rig
    {
        public readonly PlayedTransitionTracker Tracker = new();
        public readonly PluginWriteTracker WriteTracker = new();
        public readonly HandlerStats Stats = new();
        public readonly List<(string User, string Item)> Handled = new();
        public bool Scope;
        public Exception? Throw;
        public readonly PlaybackEventProcessor Processor;

        public Rig()
        {
            Processor = new PlaybackEventProcessor(Tracker, WriteTracker, (u, i) =>
            {
                Handled.Add((u, i));
                if (Throw != null) throw Throw;
                return new RemovalResult(1, 1, 1, 0, 0);
            }, Stats, () => Scope);
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
    public void WriteScope_IgnoresOurOwnEvents_NotEvenTheTrackerMemory()
    {
        var r = new Rig();
        r.Scope = true;
        Assert.Equal(PlaybackEventOutcome.WriteScope, r.Processor.Process("u", "i", "TogglePlayed", true));
        Assert.Empty(r.Handled);
        Assert.Equal(0, r.Tracker.Count);
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
        var processor = new PlaybackEventProcessor(new PlayedTransitionTracker(), new PluginWriteTracker(), (u, i) => null, new HandlerStats());
        using (WriteScope.Enter()) Assert.Equal(PlaybackEventOutcome.WriteScope, processor.Process("u", "i", "TogglePlayed", true));
        Assert.Equal(PlaybackEventOutcome.Handled, processor.Process("u", "i", "TogglePlayed", true));
    }

    // ---- Écho reconnu par PluginWriteTracker (#21) ------------------------------------------------------------

    [Fact]
    public void RecognizedEcho_ReturnsEcho_NeverCallsTheEngine()
    {
        var r = new Rig();
        r.WriteTracker.Register("u", "i");
        Assert.Equal(PlaybackEventOutcome.Echo, r.Processor.Process("u", "i", "TogglePlayed", true));
        Assert.Empty(r.Handled);
        Assert.Equal(0, r.Stats.Snapshot().Count);
    }

    [Fact]
    public void RecognizedEcho_StillUpdatesTheTransitionMemory()
    {
        // Sans cette mise à jour, une VRAIE transition future sur ce couple serait mal détectée (S6a-c).
        var r = new Rig();
        r.WriteTracker.Register("u", "i");
        r.Processor.Process("u", "i", "TogglePlayed", true); // écho : mémorise played=true sans appeler le moteur
        Assert.Equal(1, r.Tracker.Count);
        // La transition réelle suivante (un autre motif, même valeur déjà connue) n'est plus une transition.
        Assert.Equal(PlaybackEventOutcome.NoTransition, r.Processor.Process("u", "i", "PlaybackFinished", true));
        Assert.Empty(r.Handled);
    }

    [Fact]
    public void Echo_IsConsumedOnlyOnce_TheSecondEventIsProcessedNormally()
    {
        var r = new Rig();
        r.WriteTracker.Register("u", "i");
        Assert.Equal(PlaybackEventOutcome.Echo, r.Processor.Process("u", "i", "TogglePlayed", true));
        // Le compte est consommé : un second TogglePlayed(true) n'est plus un écho, et reste une transition certaine.
        Assert.Equal(PlaybackEventOutcome.Handled, r.Processor.Process("u", "i", "TogglePlayed", true));
        Assert.Single(r.Handled);
    }

    [Fact]
    public void EchoOnADifferentPair_DoesNotAffectAnUnrelatedTransition()
    {
        var r = new Rig();
        r.WriteTracker.Register("u1", "i1");
        Assert.Equal(PlaybackEventOutcome.Handled, r.Processor.Process("u2", "i2", "TogglePlayed", true));
        Assert.Single(r.Handled);
    }

    [Fact]
    public void WriteScopeIsCheckedBeforeTheWriteTracker_OrderOfGuards()
    {
        var r = new Rig();
        r.WriteTracker.Register("u", "i");
        r.Scope = true;
        Assert.Equal(PlaybackEventOutcome.WriteScope, r.Processor.Process("u", "i", "TogglePlayed", true));
        // Le WriteScope a coupé avant le tracker : l'entrée reste enregistrée, consommée à l'appel suivant.
        r.Scope = false;
        Assert.Equal(PlaybackEventOutcome.Echo, r.Processor.Process("u", "i", "TogglePlayed", true));
    }

    [Fact]
    public void DefaultWriteTracker_ConsumesARegisteredEcho()
    {
        var writeTracker = new PluginWriteTracker();
        var processor = new PlaybackEventProcessor(new PlayedTransitionTracker(), writeTracker, (u, i) => null, new HandlerStats());
        writeTracker.Register("u", "i");
        Assert.Equal(PlaybackEventOutcome.Echo, processor.Process("u", "i", "TogglePlayed", true));
    }
}
