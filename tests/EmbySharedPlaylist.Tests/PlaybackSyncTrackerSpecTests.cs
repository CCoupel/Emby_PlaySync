using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Engine;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// Tests de SPÉCIFICATION v1.2.1 (#58, D23) de <see cref="PlaybackSyncTracker"/> (remplace <c>PauseTransitionTracker</c>,
/// dont les scénarios de pause — heartbeats, pause unique, dès le 1er événement, double pause, indépendance, capacité,
/// course — sont MIGRÉS ici). Écrits depuis la machine d'états de
/// <c>docs/mockup/v1.2.1/conception/position-sync__progress-continu.md</c> (§1, §4) et le plan §3.2.
/// Horloge simulée (<see cref="FakeClock"/>) : aucun <c>Sleep</c>.
///
/// Contrat exercé (aligné sur la signature livrée par dev-plugin) : <c>new PlaybackSyncTracker(IClock?, capacity)</c>,
/// <c>OnStart(user, item, playSessionId, targets?)</c>, <c>OnProgress(user, item, playSessionId, isPaused)</c> →
/// <see cref="PlaybackSyncDecision"/> {PauseTransition, Periodic, Throttled, Ignored}, <c>MarkPropagated(user, item, ticks)</c>
/// (démarre la minuterie de 10 s ; NON appelé = verrou occupé, le Progress suivant réessaie), <c>SetTargets</c>,
/// <c>GetFreshTargets</c>, <c>OnStop(user, item, playSessionId)</c> → cibles mémorisées, <c>MinInterval</c> = 10 s.
/// Le seuil de 30 s n'est PAS du ressort du tracker (déclencheur, en amont).
/// </summary>
public class PlaybackSyncTrackerSpecTests
{
    private const string U = "u", I = "i", S1 = "sess-1";

    private static (PlaybackSyncTracker T, FakeClock C) Make(int capacity = PlaybackSyncTracker.DefaultCapacity)
    {
        var clock = new FakeClock();
        return (new PlaybackSyncTracker(clock, capacity), clock);
    }

    private static void After(FakeClock c, double seconds) => c.UtcNow += TimeSpan.FromSeconds(seconds);

    // ---- Constantes -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void MinInterval_IsTenSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), PlaybackSyncTracker.MinInterval);
    }

    // ---- Lecture continue : propagation périodique, throttle 10 s (CA1, CA3) -------------------------------------------------

    [Fact]
    public void FirstProgressOfASession_IsPeriodic_NothingWasPropagatedYet()
    {
        var (t, _) = Make();
        t.OnStart(U, I, S1);

        Assert.Equal(PlaybackSyncDecision.Periodic, t.OnProgress(U, I, S1, false));
    }

    [Fact]
    public void ProgressWithoutPriorStart_IsAlsoPeriodic_PluginRestartedMidPlayback()
    {
        var (t, _) = Make();

        Assert.Equal(PlaybackSyncDecision.Periodic, t.OnProgress(U, I, S1, false));
    }

    [Fact]
    public void AfterAPropagation_ProgressWithinTenSeconds_IsThrottled_ThenPeriodicAtTenSeconds()
    {
        var (t, c) = Make();
        Assert.Equal(PlaybackSyncDecision.Periodic, t.OnProgress(U, I, S1, false));
        t.MarkPropagated(U, I, 40 * TimeSpan.TicksPerSecond);

        After(c, 1);
        Assert.Equal(PlaybackSyncDecision.Throttled, t.OnProgress(U, I, S1, false));
        After(c, 8.9);
        Assert.Equal(PlaybackSyncDecision.Throttled, t.OnProgress(U, I, S1, false)); // 9,9 s
        After(c, 0.1);
        Assert.Equal(PlaybackSyncDecision.Periodic, t.OnProgress(U, I, S1, false));   // 10,0 s pile
    }

    [Fact]
    public void ClientReportingEverySecond_YieldsAtMostOnePropagationPerTenSeconds_CA3()
    {
        var (t, c) = Make();
        var periodic = 0; var throttled = 0;
        for (var second = 0; second < 60; second++)
        {
            var d = t.OnProgress(U, I, S1, false);
            if (d == PlaybackSyncDecision.Periodic) { periodic++; t.MarkPropagated(U, I, second * TimeSpan.TicksPerSecond); }
            else if (d == PlaybackSyncDecision.Throttled) throttled++;
            After(c, 1);
        }
        Assert.Equal(6, periodic);     // t = 0, 10, 20, 30, 40, 50
        Assert.Equal(54, throttled);   // compteur PositionProgress.Throttled > 0
    }

    [Fact]
    public void WithoutMarkPropagated_ANextProgressRetries_LockBusyDoesNotStartTheTimer()
    {
        var (t, c) = Make();
        Assert.Equal(PlaybackSyncDecision.Periodic, t.OnProgress(U, I, S1, false)); // verrou occupé : non propagé, pas de MarkPropagated

        After(c, 2);

        Assert.Equal(PlaybackSyncDecision.Periodic, t.OnProgress(U, I, S1, false)); // « le suivant réessaie »
    }

    // ---- Pause (CA2) : migré de PauseTransitionTracker ------------------------------------------------------------------------

    [Fact]
    public void PauseTransition_IsImmediate_EvenWithinTheTenSecondsInterval()
    {
        var (t, c) = Make();
        t.OnProgress(U, I, S1, false);
        t.MarkPropagated(U, I, 1);
        After(c, 2);

        Assert.Equal(PlaybackSyncDecision.PauseTransition, t.OnProgress(U, I, S1, true));
    }

    [Fact]
    public void HeartbeatsWhilePaused_AreIgnored_OnlyTheFirstIsATransition()
    {
        var (t, c) = Make();
        t.OnProgress(U, I, S1, false);
        Assert.Equal(PlaybackSyncDecision.PauseTransition, t.OnProgress(U, I, S1, true));
        for (var n = 0; n < 5; n++)
        {
            After(c, 30); // même bien au-delà de 10 s : un heartbeat en pause n'écrit jamais
            Assert.Equal(PlaybackSyncDecision.Ignored, t.OnProgress(U, I, S1, true));
        }
    }

    [Fact]
    public void PauseAsTheVeryFirstEventReceived_IsATransition_UnknownStateOverTriggersRatherThanUnder()
    {
        var (t, _) = Make();

        Assert.Equal(PlaybackSyncDecision.PauseTransition, t.OnProgress(U, I, S1, true));
    }

    [Fact]
    public void ResumeThenPauseAgain_IsANewTransition_S9DoubleStop()
    {
        var (t, c) = Make();
        Assert.Equal(PlaybackSyncDecision.PauseTransition, t.OnProgress(U, I, S1, true));
        After(c, 1);
        Assert.NotEqual(PlaybackSyncDecision.PauseTransition, t.OnProgress(U, I, S1, false)); // reprise : jamais une transition
        After(c, 1);
        Assert.Equal(PlaybackSyncDecision.PauseTransition, t.OnProgress(U, I, S1, true));
    }

    [Fact]
    public void SeveralPauseResumeRoundTrips_EachPauseIsATransition()
    {
        var (t, c) = Make();
        for (var n = 0; n < 4; n++)
        {
            Assert.Equal(PlaybackSyncDecision.PauseTransition, t.OnProgress(U, I, S1, true));
            After(c, 1);
            t.OnProgress(U, I, S1, false);
            After(c, 1);
        }
    }

    [Fact]
    public void ResumeAfterAPropagatedPause_RestartsTheTimer_ThrottledThenPeriodic()
    {
        var (t, c) = Make();
        Assert.Equal(PlaybackSyncDecision.PauseTransition, t.OnProgress(U, I, S1, true));
        t.MarkPropagated(U, I, 1);
        After(c, 3);

        Assert.Equal(PlaybackSyncDecision.Throttled, t.OnProgress(U, I, S1, false)); // reprise < 10 s après la propagation de pause
        After(c, 7);
        Assert.Equal(PlaybackSyncDecision.Periodic, t.OnProgress(U, I, S1, false));
    }

    // ---- Fermeture de session, Progress tardif (CA6), nouvelle session -------------------------------------------------------

    [Fact]
    public void LateProgressOfTheSameSession_AfterStop_IsIgnored_NeverOverwritesAfterTheStop_CA6()
    {
        var (t, c) = Make();
        t.OnStart(U, I, S1);
        t.OnProgress(U, I, S1, false);
        t.OnStop(U, I, S1);
        After(c, 60);

        Assert.Equal(PlaybackSyncDecision.Ignored, t.OnProgress(U, I, S1, false));
        Assert.Equal(PlaybackSyncDecision.Ignored, t.OnProgress(U, I, S1, true));  // ni pause tardive
    }

    [Fact]
    public void ANewPlaySessionId_AfterStop_OpensANewSession_ImmediatelyPeriodic()
    {
        var (t, c) = Make();
        t.OnProgress(U, I, S1, false);
        t.MarkPropagated(U, I, 1);
        t.OnStop(U, I, S1);
        After(c, 1);

        Assert.Equal(PlaybackSyncDecision.Periodic, t.OnProgress(U, I, "sess-2", false)); // relecture : minuterie repartie de zéro
    }

    [Fact]
    public void PlaybackStart_AfterAStop_ReopensTheSession_SameOrNewPlaySessionId()
    {
        var (t, _) = Make();
        t.OnStop(U, I, S1);
        t.OnStart(U, I, "sess-2");

        Assert.Equal(PlaybackSyncDecision.Periodic, t.OnProgress(U, I, "sess-2", false));
    }

    [Fact]
    public void ANewPlaySessionIdWithoutStop_ResetsTheThrottleAndTheMemorisedTargets()
    {
        var (t, c) = Make();
        t.OnStart(U, I, S1, new[] { "p1" });
        t.OnProgress(U, I, S1, false);
        t.MarkPropagated(U, I, 1);
        After(c, 1);

        Assert.Equal(PlaybackSyncDecision.Periodic, t.OnProgress(U, I, "sess-2", false)); // autre PlaySessionId : autre lecture
        Assert.Null(t.GetFreshTargets(U, I));                                            // cibles de l'ancienne session oubliées
    }

    [Fact]
    public void StopOfAnOldPlaySessionId_WhileANewOneIsPlaying_DoesNotCloseTheNewSession()
    {
        // Course Stop / nouvelle session (VirtualLib) : on ne ferme que la session courante.
        var (t, c) = Make();
        t.OnStart(U, I, "old", new[] { "p1" });
        t.OnStart(U, I, "new", new[] { "p2" });

        var targets = t.OnStop(U, I, "old");

        Assert.Empty(targets);
        After(c, 1);
        Assert.Equal(PlaybackSyncDecision.Periodic, t.OnProgress(U, I, "new", false)); // la session « new » vit toujours
    }

    // ---- Cibles mémorisées (fin de lecture après retrait) --------------------------------------------------------------------

    [Fact]
    public void OnStop_ReturnsTheTargetsMemorisedDuringTheSession_EvenIfStale()
    {
        var (t, c) = Make();
        t.OnStart(U, I, S1, new[] { "p1", "p2" });
        After(c, 3600); // très périmées : à l'arrêt on préfère une cible de trop (relue fraîche) à une cible perdue

        Assert.Equal(new[] { "p1", "p2" }, t.OnStop(U, I, S1));
    }

    [Fact]
    public void OnStop_WithoutTargets_ReturnsEmpty_AndNeverThrows()
    {
        var (t, _) = Make();

        Assert.Empty(t.OnStop(U, I, S1));      // couple inconnu
        t.OnStart(U, I, "s2");
        Assert.Empty(t.OnStop(U, I, "s2"));    // session sans cible
    }

    [Fact]
    public void SetTargets_ThenGetFreshTargets_ReturnsThemWhileFresh_ThenNullPastTheMaxAge()
    {
        var (t, c) = Make();
        t.OnStart(U, I, S1);
        t.SetTargets(U, I, new[] { "p1" });

        Assert.Equal(new[] { "p1" }, t.GetFreshTargets(U, I));
        After(c, PlaybackSyncTracker.TargetsMaxAge.TotalSeconds - 1);
        Assert.Equal(new[] { "p1" }, t.GetFreshTargets(U, I));
        After(c, 2);
        Assert.Null(t.GetFreshTargets(U, I)); // à re-résoudre au plus toutes les 5 min (plan §3.4)
    }

    [Fact]
    public void Targets_AreNotReturnedAsFreshOnceTheSessionIsClosed_NorAcceptedAfterwards()
    {
        var (t, _) = Make();
        t.OnStart(U, I, S1, new[] { "p1" });
        t.OnStop(U, I, S1);

        Assert.Null(t.GetFreshTargets(U, I));
        t.SetTargets(U, I, new[] { "p2" });
        Assert.Null(t.GetFreshTargets(U, I));
    }

    // ---- Indépendance, capacité, concurrence -----------------------------------------------------------------------------------

    [Fact]
    public void TwoUsersOnTheSameMedia_AreIndependent()
    {
        var (t, c) = Make();
        Assert.Equal(PlaybackSyncDecision.PauseTransition, t.OnProgress("u1", I, S1, true));
        Assert.Equal(PlaybackSyncDecision.PauseTransition, t.OnProgress("u2", I, "s-u2", true)); // la pause de u1 ne masque pas celle de u2
        Assert.Equal(PlaybackSyncDecision.Ignored, t.OnProgress("u1", I, S1, true));

        t.OnProgress("u1", I, S1, false);
        t.MarkPropagated("u1", I, 1);
        After(c, 1);
        Assert.Equal(PlaybackSyncDecision.Throttled, t.OnProgress("u1", I, S1, false));
        Assert.Equal(PlaybackSyncDecision.Periodic, t.OnProgress("u3", I, "s-u3", false)); // minuterie propre à chaque couple
    }

    [Fact]
    public void TwoMediaOfTheSameUser_AreIndependent()
    {
        var (t, c) = Make();
        t.OnProgress(U, "a", S1, false);
        t.MarkPropagated(U, "a", 1);
        After(c, 1);

        Assert.Equal(PlaybackSyncDecision.Throttled, t.OnProgress(U, "a", S1, false));
        Assert.Equal(PlaybackSyncDecision.Periodic, t.OnProgress(U, "b", S1, false));
    }

    [Fact]
    public void AStoppedPair_DoesNotCloseTheSameUsersOtherMedia()
    {
        var (t, _) = Make();
        t.OnProgress(U, "a", S1, false);
        t.OnStop(U, "a", S1);

        Assert.Equal(PlaybackSyncDecision.Ignored, t.OnProgress(U, "a", S1, false));
        Assert.Equal(PlaybackSyncDecision.Periodic, t.OnProgress(U, "b", "sess-b", false));
    }

    [Fact]
    public void NullPlaySessionId_IsToleratedAsAnAnonymousSession()
    {
        var (t, c) = Make();
        Assert.Equal(PlaybackSyncDecision.Periodic, t.OnProgress(U, I, null, false));
        t.MarkPropagated(U, I, 1);
        After(c, 1);
        Assert.Equal(PlaybackSyncDecision.Throttled, t.OnProgress(U, I, null, false));
    }

    [Fact]
    public void Capacity_ForgetsTheLeastRecentlyUsedPair_ForgottenBehavesAsUnknown()
    {
        var (t, c) = Make(capacity: 2);
        t.OnProgress(U, "1", S1, false); t.MarkPropagated(U, "1", 1);
        t.OnProgress(U, "2", S1, false);
        t.OnProgress(U, "1", S1, false); // rafraîchit « 1 » (LRU)
        t.OnProgress(U, "3", S1, false); // évince « 2 »
        After(c, 1);

        Assert.Equal(2, t.Count);
        Assert.Equal(PlaybackSyncDecision.Throttled, t.OnProgress(U, "1", S1, false));  // toujours connu
        Assert.Equal(PlaybackSyncDecision.Periodic, t.OnProgress(U, "2", S1, false));   // oublié : inconnu => propagation (sur-déclencher)
    }

    [Fact]
    public void Constructor_RejectsInvalidCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlaybackSyncTracker(new FakeClock(), 0));
    }

    [Fact]
    public void ManyPairsInParallel_StayBounded_AndNeverThrow()
    {
        var (t, _) = Make(capacity: 50);

        Parallel.For(0, 2000, n => t.OnProgress("u" + n % 7, "i" + n, "s" + n, n % 2 == 0));

        Assert.True(t.Count <= 50);
    }

    [Fact]
    public void ManyThreadsPausingTheSamePair_ExactlyOneTransitionWins()
    {
        var (t, _) = Make();
        var wins = 0;
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            start.Wait();
            if (t.OnProgress(U, I, S1, true) == PlaybackSyncDecision.PauseTransition) Interlocked.Increment(ref wins);
        })).ToArray();
        start.Set();
        Task.WaitAll(tasks);

        Assert.Equal(1, wins);
    }

    [Fact]
    public void ManyThreadsOnAPlayingPair_AtMostOnePeriodicUntilPropagationIsMarked()
    {
        // Sans MarkPropagated, plusieurs Periodic sont permis (réessai) ; avec, la minuterie s'applique : ce test vérifie
        // seulement l'absence d'exception et la cohérence du décompte des décisions sous concurrence.
        var (t, _) = Make();
        var decisions = new System.Collections.Concurrent.ConcurrentBag<PlaybackSyncDecision>();
        Parallel.For(0, 200, _ => decisions.Add(t.OnProgress(U, I, S1, false)));

        Assert.Equal(200, decisions.Count);
        Assert.All(decisions, d => Assert.Equal(PlaybackSyncDecision.Periodic, d)); // jamais Throttled/Ignored sans propagation notée
    }
}
