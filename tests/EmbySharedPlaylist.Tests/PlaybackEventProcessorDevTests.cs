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
    public void WriteScope_IgnoresOurOwnEvents_ButStillUpdatesTheTrackerMemory()
    {
        // INVERSÉ (S9f, correctif dev-plugin) : l'événement ignoré par WriteScope n'appelle jamais le moteur, mais la mémoire de
        // transition est mise à jour (un « lu » natif d'origine plugin doit être connu, sinon une relecture serait une transition).
        var r = new Rig();
        r.Scope = true;
        Assert.Equal(PlaybackEventOutcome.WriteScope, r.Processor.Process("u", "i", "TogglePlayed", true));
        Assert.Empty(r.Handled);
        Assert.Equal(0, r.Stats.Snapshot().Count);
        Assert.Equal(1, r.Tracker.Count);
    }

    [Fact]
    public void AfterAWriteScopeIgnoredNativePlayed_ANextReplayIsNoTransition()
    {
        var r = new Rig();
        r.Scope = true;
        Assert.Equal(PlaybackEventOutcome.WriteScope, r.Processor.Process("u", "i", "PlaybackProgress", true));   // lu natif, origine plugin
        r.Scope = false;
        Assert.Equal(PlaybackEventOutcome.NoTransition, r.Processor.Process("u", "i", "PlaybackFinished", true));
        Assert.Empty(r.Handled);
    }

    [Fact]
    public void AfterAWriteScopeIgnoredNativePlayed_ARealLaterUserTransitionIsStillClassifiedAndHandled()
    {
        var r = new Rig();
        r.Scope = true;
        r.Processor.Process("u", "i", "PlaybackProgress", true);                                                // ignoré, mémoire = lu
        r.Scope = false;
        Assert.Equal(PlaybackEventOutcome.NoTransition, r.Processor.Process("u", "i", "TogglePlayed", false));  // l'utilisateur décoche
        Assert.Equal(PlaybackEventOutcome.Handled, r.Processor.Process("u", "i", "TogglePlayed", true));        // puis recoche : vraie transition
        Assert.Equal(new[] { ("u", "i") }, r.Handled);
    }

    [Fact]
    public void AfterAWriteScopeIgnoredNativePlayed_ATogglePlayedTrueIsStillACertainTransition()
    {
        var r = new Rig();
        r.Scope = true;
        r.Processor.Process("u", "i", "PlaybackProgress", true);
        r.Scope = false;
        Assert.Equal(PlaybackEventOutcome.Handled, r.Processor.Process("u", "i", "TogglePlayed", true));        // geste volontaire, même si la mémoire dit « lu »
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
    public void TheWriteTrackerIsCheckedBeforeWriteScope_OrderOfGuards()
    {
        // Reproduit le cas réel (revue A1) : EmbyUserDataGateway.MarkPlayed enregistre PUIS écrit sous WriteScope ; si le SDK
        // émet l'écho de façon synchrone, il arrive PENDANT ce WriteScope. Le tracker doit gagner, sinon l'entrée reste
        // "pending" jusqu'au TTL (5 min) et une action réelle ultérieure sur ce couple serait mal classée en écho.
        var r = new Rig();
        r.WriteTracker.Register("u", "i");
        r.Scope = true; // écho survenant PENDANT le WriteScope de l'écriture qui l'a causé
        Assert.Equal(PlaybackEventOutcome.Echo, r.Processor.Process("u", "i", "TogglePlayed", true));
        Assert.Equal(0, r.WriteTracker.Count); // consommée immédiatement, jamais laissée en attente
    }

    [Fact]
    public void WriteScope_IsStillAFallback_ForAnUnregisteredEcho()
    {
        // Aucune écriture actuelle ne passe par ce chemin (tout écrit via PluginWriteTracker), mais le repli reste actif.
        var r = new Rig();
        r.Scope = true;
        Assert.Equal(PlaybackEventOutcome.WriteScope, r.Processor.Process("u", "i", "TogglePlayed", true));
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
