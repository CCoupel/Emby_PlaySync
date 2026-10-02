using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Engine;
using EmbySharedPlaylist.Reconciliation;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// Tests de SPÉCIFICATION v1.2.1 (#58, D23) de la CHAÎNE tracker + moteur de position + moteur du lu, rejouant les scénarios
/// de la maquette de conception <c>position-sync__progress-continu.md</c> — dont <b>S9g, le cas du bug</b> : lecture
/// continue sans pause jusqu'au bout avec <c>propager-lu=OUI</c> et <c>remove-si-lu=OUI</c> (CA1, CA3, CA4, CA5, CA6, CA7).
///
/// <see cref="Session"/> reproduit, en mémoire, l'orchestration du <c>PlaybackSessionListener</c> (adaptateur SDK non
/// injectable, comme aux versions précédentes) telle que décrite au plan §3.1/§3.2 : Start → cibles résolues et mémorisées ;
/// Progress → décision du tracker → (Periodic|PauseTransition) moteur avec les cibles mémorisées → <c>MarkPropagated</c> ;
/// Stopped → <c>OnStop</c> (cibles) → moteur <c>Completion</c> (si <c>PlayedToCompletion</c>) ou <c>Stop</c>. Le seuil de 30 s
/// (position absolue) est appliqué ici comme par le déclencheur. La réalité de l'adaptateur est validée par
/// <c>tests/integration/22-avancement.sh</c> (API synthétique Sessions/Playing*) puis par la procédure manuelle.
///
/// Séquence d'Emby à la fin naturelle (plan §2) : UserData du déclencheur (lu, position 0) → flux du lu IMMÉDIAT (MarkPlayed
/// chez les membres, retrait si remove-si-lu) → PUIS <c>PlaybackStopped</c>.
/// </summary>
public class ContinuousProgressChainSpecTests
{
    private const long Sec = TimeSpan.TicksPerSecond;
    private const long Min = TimeSpan.TicksPerMinute;
    private static readonly long Threshold = TimeSpan.FromSeconds(30).Ticks;

    private sealed class Rig
    {
        public readonly FakeGateway Gateway = new();
        public readonly FakeUserDataGateway UserData = new();
        public readonly PluginWriteTracker WriteTracker = new();
        public readonly SeenPlaylists Seen = new();
        public readonly PlaylistLocks Locks = new();
        public readonly UserItemLocks UserItemLocks = new();
        public readonly ListJournal Journal = new();
        public readonly FakeClock Clock = new();
        public readonly PositionProgressCounters Counters = new();
        public readonly PlaybackSyncTracker Tracker;
        public readonly ReadRemovalEngine ReadEngine;
        public readonly PlaybackPositionEngine PositionEngine;
        public int Throttled;

        public Rig()
        {
            var timeout = TimeSpan.FromSeconds(2);
            var defaults = new DefaultsService(Gateway, Seen, Locks, Journal, "AIDE", () => 2, Clock, timeout);
            Tracker = new PlaybackSyncTracker(Clock);
            ReadEngine = new ReadRemovalEngine(Gateway, UserData, WriteTracker, defaults, Seen, Locks, Journal, Clock, timeout, userItemLocks: UserItemLocks);
            PositionEngine = new PlaybackPositionEngine(Gateway, UserData, WriteTracker, defaults, Seen, Locks, Journal, Clock, timeout,
                userItemLocks: UserItemLocks, progressCounters: Counters);
        }

        /// <summary>Playlist déjà vue, propriétaire « o », déclencheur « u », membre « v ».</summary>
        public FakeGateway.State Playlist(string id, string tagsCsv, params string[] items)
        {
            var s = Gateway.Add(id, tagsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            s.Overview = "déjà";
            s.Items = (items.Length == 0 ? new[] { "F1" } : items).ToList();
            s.Members = new List<string> { "o", "u", "v" };
            Seen.TryMarkSeen(id);
            return s;
        }

        public void Advance(double seconds) => Clock.UtcNow += TimeSpan.FromSeconds(seconds);

        public Session Play(string user = "u", string item = "F1", string psid = "S1") => new(this, user, item, psid);
    }

    /// <summary>Réplique mémoire de l'orchestration de <c>PlaybackSessionListener</c> (plan §3.1/§3.2).</summary>
    private sealed class Session
    {
        private readonly Rig _r;
        private readonly string _user, _item, _psid;

        public Session(Rig r, string user, string item, string psid) { _r = r; _user = user; _item = item; _psid = psid; }

        public void Start() => _r.Tracker.OnStart(_user, _item, _psid, _r.PositionEngine.ResolveTargets(_user, _item));

        public PlaybackSyncDecision Progress(long ticks, bool paused = false)
        {
            var d = _r.Tracker.OnProgress(_user, _item, _psid, paused);
            if (d == PlaybackSyncDecision.Throttled) { _r.Throttled++; return d; }
            if (d == PlaybackSyncDecision.Ignored || ticks < Threshold) return d;
            var trigger = d == PlaybackSyncDecision.PauseTransition ? PositionTrigger.Pause : PositionTrigger.Periodic;
            var targets = _r.Tracker.GetFreshTargets(_user, _item);
            Func<bool>? isOpen = trigger == PositionTrigger.Periodic ? () => _r.Tracker.IsOpen(_user, _item, _psid) : null;
            var result = _r.PositionEngine.Handle(_user, _item, ticks, trigger, targets, isOpen);
            // M1 : les cibles ne sont (re)datées que par une VRAIE résolution (targets == null), comme le listener.
            if (targets == null && result.CandidatePlaylistIds != null) _r.Tracker.SetTargets(_user, _item, result.CandidatePlaylistIds);
            if (result.LockBusy == 0) _r.Tracker.MarkPropagated(_user, _item, ticks);
            return d;
        }

        /// <summary>Décision d'un Progress SANS exécuter le moteur (simule un Progress « en vol » décidé avant un Stop).</summary>
        public PlaybackSyncDecision Decide(bool paused = false) => _r.Tracker.OnProgress(_user, _item, _psid, paused);

        /// <summary>Exécution tardive du moteur pour un Progress périodique déjà décidé, avec la garde de session du listener.</summary>
        public void LatePeriodic(long ticks) =>
            _r.PositionEngine.Handle(_user, _item, ticks, PositionTrigger.Periodic, _r.Tracker.GetFreshTargets(_user, _item),
                () => _r.Tracker.IsOpen(_user, _item, _psid));

        public void Stopped(long ticks, bool playedToCompletion)
        {
            var targets = _r.Tracker.OnStop(_user, _item, _psid);
            if (playedToCompletion) _r.PositionEngine.Handle(_user, _item, ticks, PositionTrigger.Completion, targets);
            else if (ticks >= Threshold) _r.PositionEngine.Handle(_user, _item, ticks, PositionTrigger.Stop, targets);
        }

        /// <summary>Arrêt tel que le listener le traite : <c>completed = PlayedToCompletion || repli pur</c> (données du déclencheur relues).</summary>
        public void StoppedWithFallback(long ticks, bool playedToCompletionFlag, long runtimeTicks)
        {
            var completed = playedToCompletionFlag ||
                PlaybackCompletion.LooksCompleted(runtimeTicks, ticks, _r.UserData.IsPlayed(_user, _item), _r.UserData.GetPosition(_user, _item));
            Stopped(ticks, completed);
        }
    }

    /// <summary>Lecture continue de 0 à <paramref name="toMinutes"/> min, un Progress par <paramref name="everySeconds"/> s, sans pause.</summary>
    private static void PlayContinuously(Rig r, Session s, int toMinutes, int everySeconds = 10)
    {
        for (var t = 0; t <= toMinutes * 60; t += everySeconds)
        {
            s.Progress(t * Sec);
            r.Advance(everySeconds);
        }
    }

    // ---- CA1 : le membre suit l'avancement d'une lecture continue SANS pause -------------------------------------------------

    [Fact]
    public void CA1_ContinuousPlaybackWithoutPause_TheMemberFollowsThePosition_WithinTwentySeconds()
    {
        var r = new Rig();
        r.Playlist("p", "propager-avancement=OUI");
        var s = r.Play(); s.Start();

        for (var t = 0; t <= 120; t += 10)
        {
            s.Progress(t * Sec);
            if (t >= 30)
            {
                var expectedAtLeast = (t - 20) * Sec; // ≤ ~20 s de retard
                Assert.True(r.UserData.GetPosition("v", "F1") >= expectedAtLeast, $"à t={t} s le membre est à {r.UserData.GetPosition("v", "F1") / Sec} s");
            }
            r.Advance(10);
        }
        Assert.Equal(120 * Sec, r.UserData.GetPosition("v", "F1"));
        Assert.Empty(r.Journal.Of("PositionPropagation")); // aucune entrée de journal pour les Progress périodiques (CA8)
    }

    [Fact]
    public void CA1_ProgressBelowThirtySeconds_NeverWritesAnything_TheStartupProgressAtZero()
    {
        var r = new Rig();
        r.Playlist("p", "propager-avancement=OUI");
        var s = r.Play(); s.Start();

        s.Progress(0); r.Advance(10);
        s.Progress(10 * Sec); r.Advance(10);
        s.Progress(20 * Sec);

        Assert.Equal(0, r.UserData.SetPositionCalls);
    }

    // ---- CA2 : pause immédiate, heartbeat en pause silencieux ------------------------------------------------------------------

    [Fact]
    public void CA2_PauseIsImmediate_AndHeartbeatsWhilePausedWriteNothing()
    {
        var r = new Rig();
        r.Playlist("p", "propager-avancement=OUI");
        var s = r.Play(); s.Start();
        s.Progress(40 * Sec); r.Advance(2);
        s.Progress(42 * Sec, paused: true);   // pause 2 s après la propagation périodique : immédiate

        Assert.Equal(42 * Sec, r.UserData.GetPosition("v", "F1"));
        var entry = r.Journal.Of("PositionPropagation").Single();
        Assert.EndsWith("trigger=pause", entry.Detail);

        var writes = r.UserData.SetPositionCalls;
        for (var n = 0; n < 6; n++) { r.Advance(30); s.Progress(42 * Sec, paused: true); } // heartbeats
        Assert.Equal(writes, r.UserData.SetPositionCalls);
        Assert.Single(r.Journal.Of("PositionPropagation"));
    }

    // ---- CA3 : au plus une propagation / 10 s / couple -------------------------------------------------------------------------

    [Fact]
    public void CA3_ClientReportingEverySecond_AtMostOnePropagationPerTenSeconds()
    {
        var r = new Rig();
        r.Playlist("p", "propager-avancement=OUI");
        var s = r.Play(); s.Start();

        for (var t = 30; t < 90; t++) { s.Progress(t * Sec); r.Advance(1); }

        Assert.Equal(6, r.UserData.PositionsSet.Count(p => p.UserId == "v")); // 60 s de lecture => 6 écritures chez « v »
        Assert.Equal(54, r.Throttled);                                        // compteur Throttled > 0
        Assert.Equal(6, r.Counters.Snapshot().Propagated);                    // Diagnostics/State.PositionProgress.Propagated
    }

    // ---- CA4 / S9g : le cas du bug ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("propager-avancement=OUI,propager-lu=OUI,remove-si-lu=OUI")]   // S9g : le média est retiré AVANT l'arrêt
    [InlineData("propager-avancement=OUI,propager-lu=OUI")]                    // sans retrait
    public void CA4_S9g_ContinuousPlaybackToTheEnd_TheMemberEndsPlayedAndAtZero_NoResume(string tags)
    {
        var r = new Rig();
        var playlist = r.Playlist("p", tags);
        var s = r.Play(); s.Start();
        PlayContinuously(r, s, toMinutes: 100);              // U1 lit F1 (100 min) sans aucune pause
        Assert.True(r.UserData.GetPosition("v", "F1") >= 99 * Min, "avant la fin, le membre suit la lecture");

        // Fin naturelle, séquence Emby : UserData U1 (lu, position 0) -> flux du lu IMMÉDIAT -> PUIS PlaybackStopped.
        r.UserData.SetPlayed("u", "F1", true);
        r.UserData.SetPosition("u", "F1", 0);
        r.ReadEngine.Handle("u", "F1");                      // MarkPlayed chez les membres ; retrait du média si remove-si-lu=OUI
        Assert.Equal(!tags.Contains("remove-si-lu"), playlist.Items.Contains("F1"));
        s.Stopped(ticks: 100 * Min, playedToCompletion: true);

        Assert.True(r.UserData.IsPlayed("v", "F1"));                      // « lu » chez le membre
        Assert.Equal(0L, r.UserData.GetPosition("v", "F1"));               // ET aucun point de reprise (pas de « Reprendre »)
        Assert.Equal(0L, r.UserData.GetPosition("o", "F1"));
    }

    [Fact]
    public void Documentation_CA4_WithoutMemorisedTargets_TheEngineFindsNoPlaylistAfterRemoval_NotARegressionTestOfTheFix()
    {
        // TEST DE DOCUMENTATION (revue m2) : il décrit le comportement du moteur appelé avec targets:null — la cause racine n°2
        // de v1.2.0 — et NE prouve rien du correctif (la preuve est CA4_S9g_… ci-dessus et I42 en intégration). Reproduction de la cause racine n°2 : si la session ne mémorisait PAS les cibles, la playlist n'est plus trouvée après
        // le retrait et la position périmée reste. Ce test documente le symptôme rapporté (« lu posé, position non mise à jour »).
        var r = new Rig();
        r.Playlist("p", "propager-avancement=OUI,propager-lu=OUI,remove-si-lu=OUI");
        r.UserData.SetPosition("v", "F1", 95 * Min);
        r.ReadEngine.Handle("u", "F1");

        r.PositionEngine.Handle("u", "F1", 100 * Min, PositionTrigger.Completion, targets: null);

        Assert.Equal(95 * Min, r.UserData.GetPosition("v", "F1"));
    }

    // ---- CA5 : fin de lecture sans propager-lu ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("propager-avancement=OUI")]
    [InlineData("propager-avancement=OUI,propager-lu=NON,remove-si-lu=OUI")]
    public void CA5_ContinuousPlaybackToTheEnd_WithoutPropagerLu_TheMemberIsNotPlayed_AndKeepsTheStopPosition(string tags)
    {
        var r = new Rig();
        r.Playlist("p", tags);
        var s = r.Play(); s.Start();
        PlayContinuously(r, s, toMinutes: 100);
        r.UserData.SetPlayed("u", "F1", true);
        r.UserData.SetPosition("u", "F1", 0);
        r.ReadEngine.Handle("u", "F1");                      // propager-lu absent/NON : ni flag ni retrait (S3b)

        s.Stopped(ticks: 99 * Min + 40 * Sec, playedToCompletion: true);

        Assert.False(r.UserData.IsPlayed("v", "F1") == true);
        Assert.Equal(99 * Min + 40 * Sec, r.UserData.GetPosition("v", "F1")); // S9f inchangé : « Reprendre à 99 % »
    }

    // ---- CA6 : Progress tardif après l'arrêt --------------------------------------------------------------------------------------

    [Fact]
    public void CA6_ALateProgressOfTheSameSession_AfterTheCompletion_NeverRewritesThePosition()
    {
        var r = new Rig();
        r.Playlist("p", "propager-avancement=OUI,propager-lu=OUI");
        var s = r.Play(); s.Start();
        PlayContinuously(r, s, toMinutes: 100);
        s.Stopped(ticks: 100 * Min, playedToCompletion: true);
        Assert.Equal(0L, r.UserData.GetPosition("v", "F1"));
        var writes = r.UserData.SetPositionCalls;

        r.Advance(30);
        var late = s.Progress(100 * Min - 5 * Sec);            // Progress tardif du MÊME PlaySessionId

        Assert.Equal(PlaybackSyncDecision.Ignored, late);
        Assert.Equal(writes, r.UserData.SetPositionCalls);
        Assert.Equal(0L, r.UserData.GetPosition("v", "F1"));   // la remise à 0 n'est jamais ré-écrasée par 99 %
    }

    [Fact]
    public void CA6_ARereadOfTheSameMedia_OpensANewSession_AndPropagatesAgain()
    {
        var r = new Rig();
        r.Playlist("p", "propager-avancement=OUI,propager-lu=OUI");
        var first = r.Play(psid: "S1"); first.Start();
        PlayContinuously(r, first, toMinutes: 2);
        first.Stopped(ticks: 2 * Min, playedToCompletion: true);

        var second = r.Play(psid: "S2"); second.Start();     // S9d : relecture d'un média déjà lu
        second.Progress(40 * Sec);

        Assert.Equal(40 * Sec, r.UserData.GetPosition("v", "F1"));
    }

    // ---- Arrêt non terminé ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void S9c_AStopAtNinetySevenPercentNotCompleted_PropagatesTheRawStopPosition_NeverZero()
    {
        var r = new Rig();
        r.Playlist("p", "propager-avancement=OUI,propager-lu=OUI");
        var s = r.Play(); s.Start();
        PlayContinuously(r, s, toMinutes: 97);

        s.Stopped(ticks: 97 * Min + 5 * Sec, playedToCompletion: false);   // PlayedToCompletion=faux

        Assert.Equal(97 * Min + 5 * Sec, r.UserData.GetPosition("v", "F1"));
        Assert.EndsWith("trigger=stop", r.Journal.Of("PositionPropagation").Last().Detail);
    }

    // ---- CA7 : aucun écho mal classé / aucune boucle ----------------------------------------------------------------------------

    [Fact]
    public void CA7_EachPeriodicWriteRegistersExactlyOneEcho_NothingPendingAfterConsumption()
    {
        var r = new Rig();
        r.Playlist("p", "propager-avancement=OUI");
        var s = r.Play(); s.Start();

        for (var t = 30; t <= 90; t += 10)
        {
            s.Progress(t * Sec);
            Assert.True(r.WriteTracker.TryConsume("v", "F1"));    // l'écho UserDataSaved du membre est reconnu
            Assert.True(r.WriteTracker.TryConsume("o", "F1"));
            Assert.False(r.WriteTracker.TryConsume("u", "F1"));   // le déclencheur n'est jamais écrit
            r.Advance(10);
        }
        Assert.Equal(0, r.WriteTracker.Count);
    }

    [Fact]
    public void CA7_PeriodicWrites_NeverTouchThePlayedFlag_NorOtherPlaylistsOfTheMember()
    {
        var r = new Rig();
        r.Playlist("p", "propager-avancement=OUI");
        var other = r.Playlist("other", "propager-avancement=OUI,propager-lu=OUI,remove-si-lu=OUI", "F2");
        var s = r.Play(); s.Start();

        PlayContinuously(r, s, toMinutes: 3);

        Assert.Equal(0, r.UserData.MarkPlayedCalls);
        Assert.Equal(new[] { "F2" }, other.Items);                                  // S6/S7 : rien de parasite dans une autre liste
        Assert.DoesNotContain(r.UserData.PositionsSet, p => p.ItemId == "F2");
    }

    // ---- Verrou occupé, cibles, configuration ---------------------------------------------------------------------------------------

    [Fact]
    public void LockBusyOnAPeriodicProgress_IsRetriedAtTheNextProgress_NoTenSecondWait()
    {
        var r = new Rig();
        r.Playlist("p", "propager-avancement=OUI");
        var s = r.Play(); s.Start();
        var held = new ManualResetEventSlim(); var release = new ManualResetEventSlim();
        var t = new Thread(() => { using var l = r.Locks.TryAcquire("p", TimeSpan.FromSeconds(5)); held.Set(); release.Wait(); });
        t.Start(); held.Wait();
        try { s.Progress(40 * Sec); }                       // verrou occupé : ignoré, LastPropagatedAt non mis à jour
        finally { release.Set(); t.Join(); }
        Assert.Equal(0, r.UserData.SetPositionCalls);

        r.Advance(1);
        s.Progress(41 * Sec);                               // le suivant réessaie immédiatement (pas de minuterie de 10 s)

        Assert.Equal(41 * Sec, r.UserData.GetPosition("v", "F1"));
        Assert.Equal(1, r.Counters.Snapshot().LockBusy);    // Diagnostics/State.PositionProgress.LockBusy
    }

    [Fact]
    public void SessionTargets_AreResolvedOnceAtStart_ThenTheMarkersAreReadFreshAtEachEvent()
    {
        var r = new Rig();
        var p = r.Playlist("p", "propager-avancement=OUI");
        var s = r.Play(); s.Start();
        s.Progress(40 * Sec); r.Advance(10);
        Assert.Equal(40 * Sec, r.UserData.GetPosition("v", "F1"));

        p.Tags = new List<string> { "propager-avancement=NON" };    // le propriétaire coupe l'option en cours de lecture
        s.Progress(50 * Sec);

        Assert.Equal(40 * Sec, r.UserData.GetPosition("v", "F1"));
    }

    [Fact]
    public void TheTriggerUserPositionIsNeverWritten_AnywhereInTheChain()
    {
        var r = new Rig();
        r.Playlist("p", "propager-avancement=OUI,propager-lu=OUI");
        var s = r.Play(); s.Start();
        PlayContinuously(r, s, toMinutes: 5);
        s.Stopped(5 * Min, playedToCompletion: true);

        Assert.DoesNotContain(r.UserData.PositionsSet, p => p.UserId == "u");
    }

    // ---- M1 (revue) : cibles mémorisées datées par une VRAIE résolution, liste vide re-résolue ---------------------------------

    [Fact]
    public void M1_AMediaAddedToAPlaylistAfterTheStart_IsPickedUpAtTheNextProgress_EmptyTargetsAreReResolved()
    {
        var r = new Rig();
        var s = r.Play(); s.Start();                       // aucune playlist au démarrage : cibles = liste vide
        r.Playlist("p", "propager-avancement=OUI");        // le propriétaire ajoute F1 à une playlist partagée pendant la lecture
        r.Advance(40);

        s.Progress(40 * Sec);

        Assert.Equal(40 * Sec, r.UserData.GetPosition("v", "F1"));
        Assert.Equal(new[] { "p" }, r.Tracker.GetFreshTargets("u", "F1")); // et la résolution est mémorisée
    }

    [Fact]
    public void M1_AMediaRemovedFromThePlaylistDuringPlayback_StopsBeingSynced_OnceTheTargetsExpire()
    {
        var r = new Rig();
        var playlist = r.Playlist("p", "propager-avancement=OUI");
        var s = r.Play(); s.Start();

        for (var t = 30; t <= 400; t += 10)
        {
            if (t == 50) playlist.Items.Remove("F1");      // retiré par le propriétaire en cours de lecture
            r.Clock.UtcNow = new FakeClock().UtcNow + TimeSpan.FromSeconds(t);
            s.Progress(t * Sec);
        }

        // Tant que les cibles sont fraîches (< 5 min) la copie continue (fenêtre acceptée) ; au-delà elles sont re-résolues,
        // la playlist ne contient plus le média : plus aucune écriture. Sans correctif, les Periodic rafraîchissaient la date des
        // cibles et la copie se poursuivait jusqu'à l'arrêt (400 s).
        var last = r.UserData.GetPosition("v", "F1");
        Assert.True(last <= (PlaybackSyncTracker.TargetsMaxAge.TotalSeconds + 10) * Sec, $"copie encore active à {last / Sec} s");
        Assert.True(last >= 50 * Sec);
    }

    [Fact]
    public void M1_ReusingMemorisedTargetsAtAPeriodicProgress_NeverRefreshesTheirDate()
    {
        var r = new Rig();
        r.Playlist("p", "propager-avancement=OUI");
        var s = r.Play(); s.Start();
        s.Progress(40 * Sec);                               // 1er Periodic : cibles du Start, réutilisées
        r.Advance(PlaybackSyncTracker.TargetsMaxAge.TotalSeconds + 1);

        Assert.Null(r.Tracker.GetFreshTargets("u", "F1")); // périmées malgré le Periodic intermédiaire
    }

    // ---- M2 (revue) : Progress « en vol » décidé AVANT le Stop, exécuté APRÈS : jamais d'écrasement de la position de fin ----------

    [Fact]
    public void M2_ALatePeriodicAfterTheCompletion_NeverOverwritesTheZero()
    {
        var r = new Rig();
        r.Playlist("p", "propager-avancement=OUI,propager-lu=OUI");
        var s = r.Play(); s.Start();
        PlayContinuously(r, s, toMinutes: 95);
        r.Advance(10);
        Assert.Equal(PlaybackSyncDecision.Periodic, s.Decide());        // Progress à 95 % décidé (session encore ouverte)…
        s.Stopped(100 * Min, playedToCompletion: true);                  // …le Stop/Completion passe avant : 0 chez le membre
        Assert.Equal(0L, r.UserData.GetPosition("v", "F1"));

        s.LatePeriodic(95 * Min);                                        // …puis le Periodic en vol obtient enfin ses verrous

        Assert.Equal(0L, r.UserData.GetPosition("v", "F1"));             // le membre n'est pas « lu + Reprendre à 95 % »
        Assert.Equal(0L, r.UserData.GetPosition("o", "F1"));
    }

    [Fact]
    public void M2_ALatePeriodicAfterANormalStop_NeverOverwritesTheStopPosition()
    {
        var r = new Rig();
        r.Playlist("p", "propager-avancement=OUI");
        var s = r.Play(); s.Start();
        PlayContinuously(r, s, toMinutes: 60);
        r.Advance(10);
        Assert.Equal(PlaybackSyncDecision.Periodic, s.Decide());
        s.Stopped(62 * Min, playedToCompletion: false);

        s.LatePeriodic(60 * Min);

        Assert.Equal(62 * Min, r.UserData.GetPosition("v", "F1"));
    }

    [Fact]
    public void M2_WithoutTheSessionGuard_TheLatePeriodicWouldOverwrite_DocumentationOfTheRace()
    {
        // TEST DE DOCUMENTATION : sans isSessionOpen (ancien comportement), le Periodic tardif écraserait la remise à 0.
        var r = new Rig();
        r.Playlist("p", "propager-avancement=OUI,propager-lu=OUI");
        var s = r.Play(); s.Start();
        PlayContinuously(r, s, toMinutes: 95);
        s.Stopped(100 * Min, playedToCompletion: true);

        r.PositionEngine.Handle("u", "F1", 95 * Min, PositionTrigger.Periodic);   // pas de garde

        Assert.Equal(95 * Min, r.UserData.GetPosition("v", "F1"));
    }

    // ---- m1 (revue) : repli de fin de lecture dans la chaîne ---------------------------------------------------------------------

    [Fact]
    public void Fallback_PlayedToCompletionFalse_ButTheTriggerIsPlayedAtZeroNearTheEnd_IsTreatedAsACompletion()
    {
        var r = new Rig();
        r.Playlist("p", "propager-avancement=OUI,propager-lu=OUI");
        var s = r.Play(); s.Start();
        PlayContinuously(r, s, toMinutes: 95);
        r.UserData.SetPlayed("u", "F1", true);   // Emby a déjà appliqué la fin de lecture au déclencheur : lu, position 0
        r.UserData.SetPosition("u", "F1", 0);

        s.StoppedWithFallback(99 * Min, playedToCompletionFlag: false, runtimeTicks: 100 * Min);

        Assert.Equal(0L, r.UserData.GetPosition("v", "F1"));
    }

    [Fact]
    public void Fallback_DoesNotApply_WhenTheTriggerIsNotPlayed_TheStopIsANormalOne()
    {
        var r = new Rig();
        r.Playlist("p", "propager-avancement=OUI,propager-lu=OUI");
        var s = r.Play(); s.Start();
        PlayContinuously(r, s, toMinutes: 95);

        s.StoppedWithFallback(99 * Min, playedToCompletionFlag: false, runtimeTicks: 100 * Min);   // arrêt à 99 % sans fin de lecture

        Assert.Equal(99 * Min, r.UserData.GetPosition("v", "F1"));
    }
}
