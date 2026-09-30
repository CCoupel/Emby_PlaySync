using System.Diagnostics;
using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Engine;
using EmbySharedPlaylist.Reconciliation;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// Tests de SPÉCIFICATION v1.2.1 (#58, D23) de <see cref="PlaybackPositionEngine"/> : avancement synchronisé EN CONTINU et
/// règle de FIN DE LECTURE. Écrits AVANT le code final (TDD) depuis <c>contracts/http-endpoints.md</c> (PositionPropagation,
/// <c>trigger=</c>), <c>contracts/CHANGELOG.md</c> [20260930] et la maquette de conception
/// <c>docs/mockup/v1.2.1/conception/position-sync__progress-continu.md</c> (§3 règle de fin, §4 constantes).
///
/// CONTRAT SUPPOSÉ pour dev-plugin (plan §6 tâche 3) — à signaler au teamleader en cas d'écart :
///   <c>enum PositionTrigger { Periodic, Pause, Stop, Completion }</c> (namespace EmbySharedPlaylist.Engine) ;
///   <c>Handle(string userId, string itemId, long ticks, PositionTrigger trigger = PositionTrigger.Stop,
///          IReadOnlyCollection&lt;string&gt;? targetPlaylistIds = null)</c> — le 4e paramètre par défaut <c>Stop</c> garde les
///   suites v1.2.0 (appel à 3 arguments) intactes ; <c>targetPlaylistIds</c> = playlists mémorisées pendant la session
///   (réunies aux playlists contenant encore le média) ;
///   Periodic : verrous de 250 ms (playlist ET (utilisateur, média)), ni journal <c>PositionPropagation</c> ni <c>Skipped</c> ;
///   Pause/Stop/Completion : journal inchangé + <c>trigger=pause|stop|completion</c> EN FIN de <c>Detail</c>.
/// Le seuil de 30 s n'est PAS vérifié par le moteur (il l'est par le déclencheur) : les positions ci-dessous sont donc libres.
/// </summary>
public class PlaybackPositionEngineContinuousSpecTests
{
    private const long Min = TimeSpan.TicksPerMinute;

    private sealed class Rig
    {
        public readonly FakeGateway Gateway = new();
        public readonly FakeUserDataGateway UserData = new();
        public readonly PluginWriteTracker WriteTracker = new();
        public readonly SeenPlaylists Seen = new();
        public readonly PlaylistLocks Locks = new();
        public readonly UserItemLocks UserItemLocks = new();
        public readonly ListJournal Journal = new();
        public readonly HandlerStats Handler = new();
        public readonly PlaybackPositionEngine Engine;

        /// <param name="lockTimeout">null = délai par défaut réel du moteur (5 s pour Pause/Stop/fin, 250 ms pour Periodic).</param>
        public Rig(TimeSpan? lockTimeout = null)
        {
            var defaults = new DefaultsService(Gateway, Seen, Locks, Journal, "AIDE", () => 2, null, TimeSpan.FromMilliseconds(150));
            Engine = new PlaybackPositionEngine(Gateway, UserData, WriteTracker, defaults, Seen, Locks, Journal, new FakeClock(),
                lockTimeout, handler: Handler, userItemLocks: UserItemLocks);
        }

        /// <summary>Playlist gérée : propriétaire « o », déclencheur « u », membre « v » ; le média « F1 » y figure.</summary>
        public FakeGateway.State Playlist(string id, string[] tags, params string[] items)
        {
            var s = Gateway.Add(id, tags);
            s.Overview = "x";
            s.Items = (items.Length == 0 ? new[] { "F1" } : items).ToList();
            s.Members = new List<string> { "o", "u", "v" };
            Seen.TryMarkSeen(id);
            return s;
        }

        public IEnumerable<JournalEntry> Positions => Journal.Of("PositionPropagation");
    }

    private static readonly string[] Av = { "propager-avancement=OUI" };
    private static readonly string[] AvLu = { "propager-avancement=OUI", "propager-lu=OUI" };
    private static readonly string[] AvLuRm = { "propager-avancement=OUI", "propager-lu=OUI", "remove-si-lu=OUI" };

    // ---- Periodic (PlaybackProgress en cours de lecture) : CA1, CA3, CA8 ------------------------------------------------------

    [Fact]
    public void Periodic_PropagatesTheRawPositionToTheOtherMembers()
    {
        var r = new Rig();
        r.Playlist("1", Av);

        var result = r.Engine.Handle("u", "F1", 45 * Min, PositionTrigger.Periodic);

        Assert.Equal(45 * Min, r.UserData.GetPosition("v", "F1"));
        Assert.Equal(45 * Min, r.UserData.GetPosition("o", "F1"));
        Assert.Equal(0L, r.UserData.GetPosition("u", "F1")); // le déclencheur n'est jamais écrit
        Assert.Equal(2, result.Propagated);
        Assert.Equal(0, r.UserData.MarkPlayedCalls);          // ne marque jamais « lu » (D21)
    }

    [Fact]
    public void Periodic_LeavesNoPositionPropagationEntry_CA8()
    {
        var r = new Rig();
        r.Playlist("1", Av);

        r.Engine.Handle("u", "F1", 45 * Min, PositionTrigger.Periodic);
        r.Engine.Handle("u", "F1", 46 * Min, PositionTrigger.Periodic);

        Assert.Empty(r.Positions); // 500 entrées seraient saturées en quelques minutes : compteurs, pas de journal
    }

    [Fact]
    public void Periodic_SamePosition_LeavesNoSkippedEntry_AndNoWrite()
    {
        var r = new Rig();
        r.Playlist("1", Av);
        r.Engine.Handle("u", "F1", 45 * Min, PositionTrigger.Periodic);
        var writes = r.UserData.SetPositionCalls;

        r.Engine.Handle("u", "F1", 45 * Min, PositionTrigger.Periodic);

        Assert.Equal(writes, r.UserData.SetPositionCalls);
        Assert.Empty(r.Journal.Of("Skipped"));
    }

    [Theory]
    [InlineData("propager-avancement=NON")]
    [InlineData("propager-lu=OUI")] // v1.2.0 : propager-lu n'active PAS la position
    public void Periodic_InactivePlaylist_WritesNothing_AndLeavesNoTrace(string tag)
    {
        var r = new Rig();
        r.Playlist("1", new[] { tag });

        r.Engine.Handle("u", "F1", 45 * Min, PositionTrigger.Periodic);

        Assert.Equal(0, r.UserData.SetPositionCalls);
        Assert.Empty(r.Journal.Of("Skipped"));
        Assert.Empty(r.Positions);
    }

    [Fact]
    public void Periodic_RegistersTheAntiEchoBeforeEachRealWrite_OneWriteOneEcho()
    {
        var r = new Rig();
        r.Playlist("1", Av);

        r.Engine.Handle("u", "F1", 45 * Min, PositionTrigger.Periodic);

        Assert.True(r.WriteTracker.TryConsume("v", "F1"));
        Assert.False(r.WriteTracker.TryConsume("v", "F1")); // une écriture = un écho, rien de plus
        Assert.False(r.WriteTracker.TryConsume("u", "F1")); // le déclencheur n'est jamais enregistré
    }

    [Fact]
    public void Periodic_PlaylistLockBusy_IsDroppedWithin250ms_NoWrite_NoJournal()
    {
        var r = new Rig(); // délai par défaut réel : 5 s pour un événement discret, 250 ms sur Periodic
        r.Playlist("1", Av);
        var held = new ManualResetEventSlim(); var release = new ManualResetEventSlim();
        var t = new Thread(() => { using var l = r.Locks.TryAcquire("1", TimeSpan.FromSeconds(5)); held.Set(); release.Wait(); });
        t.Start(); held.Wait();
        try
        {
            var sw = Stopwatch.StartNew();
            r.Engine.Handle("u", "F1", 45 * Min, PositionTrigger.Periodic);
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 2000, $"le pipeline de progression d'Emby ne doit jamais attendre 5 s (mesuré {sw.ElapsedMilliseconds} ms)");
            Assert.Equal(0, r.UserData.SetPositionCalls);
            Assert.Empty(r.Journal.Of("Skipped")); // « ignoré SANS journal » (compteur PositionProgress.LockBusy)
            Assert.Empty(r.Positions);
        }
        finally { release.Set(); t.Join(); }
    }

    [Fact]
    public void Periodic_UserItemLockBusyForOneMember_SkipsOnlyThatMember_WithinTheShortDelay()
    {
        var r = new Rig();
        r.Playlist("1", Av);
        var held = new ManualResetEventSlim(); var release = new ManualResetEventSlim();
        var t = new Thread(() => { using var l = r.UserItemLocks.TryAcquire("v", "F1", TimeSpan.FromSeconds(5)); held.Set(); release.Wait(); });
        t.Start(); held.Wait();
        try
        {
            var sw = Stopwatch.StartNew();
            r.Engine.Handle("u", "F1", 45 * Min, PositionTrigger.Periodic);
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 2000, $"verrou (utilisateur, média) : 250 ms, pas 5 s (mesuré {sw.ElapsedMilliseconds} ms)");
            Assert.Equal(0L, r.UserData.GetPosition("v", "F1"));       // « v » sauté, il sera traité au Progress suivant
            Assert.Equal(45 * Min, r.UserData.GetPosition("o", "F1"));  // « o » écrit malgré tout
            Assert.Empty(r.Journal.Of("Skipped"));
            Assert.False(r.WriteTracker.TryConsume("v", "F1"));         // rien d'enregistré pour le membre sauté
        }
        finally { release.Set(); t.Join(); }
    }

    [Fact]
    public void Periodic_RecordsItsDurationInHandlerStats()
    {
        var r = new Rig();
        r.Playlist("1", Av);

        r.Engine.Handle("u", "F1", 45 * Min, PositionTrigger.Periodic);

        Assert.Equal(1, r.Handler.Snapshot().Count); // budget p95 <= 300 ms vérifié via Diagnostics/State.Handler
    }

    [Fact]
    public void Periodic_WithMemorisedTargets_PropagatesToThem_ReadingTheTagsFresh()
    {
        // Plan §3.4 : une résolution à l'ouverture de session, puis réutilisation des cibles mémorisées (étiquettes relues fraîches).
        var r = new Rig();
        r.Playlist("1", Av);
        var before = r.Gateway.GetCalls;

        r.Engine.Handle("u", "F1", 45 * Min, PositionTrigger.Periodic, new[] { "1" });

        Assert.Equal(45 * Min, r.UserData.GetPosition("v", "F1"));
        Assert.True(r.Gateway.GetCalls > before); // étiquettes relues à chaque événement (relecture fraîche)
    }

    [Fact]
    public void Periodic_WithMemorisedTargets_ReadsTheTagsFresh_AFlippedMarkerStopsThePropagation()
    {
        var r = new Rig();
        var s = r.Playlist("1", Av);
        r.Engine.Handle("u", "F1", 40 * Min, PositionTrigger.Periodic, new[] { "1" });

        s.Tags = new List<string> { "propager-avancement=NON" };   // le propriétaire coupe l'option pendant la lecture
        r.Engine.Handle("u", "F1", 50 * Min, PositionTrigger.Periodic, new[] { "1" });

        Assert.Equal(40 * Min, r.UserData.GetPosition("v", "F1")); // pas d'écriture après la bascule
    }

    // ---- Pause / Stop : inchangés, trigger= en fin de Detail : CA2, CA8 -------------------------------------------------------

    [Theory]
    [InlineData(PositionTrigger.Pause, "pause")]
    [InlineData(PositionTrigger.Stop, "stop")]
    public void PauseAndStop_AreJournaled_WithTheTriggerAtTheEndOfTheDetail(PositionTrigger trigger, string expected)
    {
        var r = new Rig();
        r.Playlist("1", Av);

        r.Engine.Handle("u", "F1", 45 * Min, trigger);

        var entry = r.Positions.Single();
        Assert.Equal("u", entry.UserId);
        Assert.Equal("F1", entry.ItemId);
        Assert.Matches($@"^members=2 propagated=2 samePosition=0 noAccess=0 lockBusy=0 durationMs=\d+ trigger={expected}$", entry.Detail);
        Assert.Equal(45 * Min, r.UserData.GetPosition("v", "F1"));
    }

    [Fact]
    public void ThreeArgumentCall_KeepsTheV120BehaviourOfAStop()
    {
        // Compatibilité des suites v1.2.0 (immuables) : sans déclencheur, l'appel vaut un arrêt brut.
        var r = new Rig();
        r.Playlist("1", Av);

        r.Engine.Handle("u", "F1", 45 * Min);

        Assert.Equal(45 * Min, r.UserData.GetPosition("v", "F1"));
        Assert.EndsWith("trigger=stop", r.Positions.Single().Detail);
    }

    [Fact]
    public void Pause_DoesNotDependOnTheShortPeriodicLockDelay_ItWaitsForTheRegularDelay()
    {
        // Pause/Stop/fin gardent le délai de 5 s (événements discrets) : ici délai injecté de 150 ms, verrou libéré à ~60 ms.
        var r = new Rig(TimeSpan.FromMilliseconds(150));
        r.Playlist("1", Av);
        var held = new ManualResetEventSlim();
        var t = new Thread(() => { using var l = r.Locks.TryAcquire("1", TimeSpan.FromSeconds(5)); held.Set(); Thread.Sleep(60); });
        t.Start(); held.Wait();
        try
        {
            r.Engine.Handle("u", "F1", 45 * Min, PositionTrigger.Pause);
            Assert.Equal(45 * Min, r.UserData.GetPosition("v", "F1"));
        }
        finally { t.Join(); }
    }

    // ---- Fin de lecture (Completion) : CA4, CA5 -------------------------------------------------------------------------------

    [Theory]
    [InlineData("propager-avancement=OUI,propager-lu=OUI")]
    [InlineData("propager-avancement=OUI,propager-lu=OUI,remove-si-lu=OUI")]
    public void Completion_WithProgressAndPlayedPropagation_WritesZeroToTheMembers_CA4(string tagsCsv)
    {
        var r = new Rig();
        r.Playlist("1", tagsCsv.Split(','));
        r.UserData.SetPosition("v", "F1", 95 * Min);   // point de reprise laissé par la propagation périodique
        r.UserData.SetPosition("o", "F1", 95 * Min);

        var result = r.Engine.Handle("u", "F1", 100 * Min, PositionTrigger.Completion);

        Assert.Equal(0L, r.UserData.GetPosition("v", "F1")); // aucun « Reprendre » chez le membre
        Assert.Equal(0L, r.UserData.GetPosition("o", "F1"));
        Assert.Equal(2, result.Propagated);
        Assert.DoesNotContain(r.UserData.PositionsSet, p => p.UserId == "v" && p.Ticks == 100 * Min); // jamais la position brute
        Assert.EndsWith("trigger=completion", r.Positions.Single().Detail);
    }

    [Fact]
    public void Completion_WithProgressAndPlayedPropagation_NeverMarksPlayed_D21()
    {
        // Le « lu » est l'affaire du flux du lu (R4a/R4b, inchangé) : propager-lu n'écrit jamais de position, et inversement.
        var r = new Rig();
        r.Playlist("1", AvLu);
        r.UserData.SetPosition("v", "F1", 95 * Min);

        r.Engine.Handle("u", "F1", 100 * Min, PositionTrigger.Completion);

        Assert.Equal(0, r.UserData.MarkPlayedCalls);
    }

    [Fact]
    public void Completion_ZeroIsWrittenWhateverTheMemberPlayedState_NoCouplingWithTheReadFlow()
    {
        var r = new Rig();
        r.Playlist("1", AvLu);
        r.UserData.SetPosition("v", "F1", 95 * Min);
        r.UserData.SetPlayed("v", "F1", true);            // le flux du lu est déjà passé chez « v » (course)
        r.UserData.SetPosition("o", "F1", 95 * Min);
        r.UserData.SetPlayed("o", "F1", false);           // pas encore passé chez « o »

        r.Engine.Handle("u", "F1", 100 * Min, PositionTrigger.Completion);

        Assert.Equal(0L, r.UserData.GetPosition("v", "F1"));
        Assert.Equal(0L, r.UserData.GetPosition("o", "F1"));
    }

    [Fact]
    public void Completion_ZeroIsNotSubjectToTheThirtySecondsThreshold()
    {
        var r = new Rig();
        r.Playlist("1", AvLu);
        r.UserData.SetPosition("v", "F1", 10 * Min);

        r.Engine.Handle("u", "F1", 0, PositionTrigger.Completion); // position d'événement 0 (Emby l'a déjà remise à 0)

        Assert.Equal(0L, r.UserData.GetPosition("v", "F1"));
    }

    [Theory]
    [InlineData("propager-avancement=OUI")]
    [InlineData("propager-avancement=OUI,propager-lu=NON")]
    [InlineData("propager-avancement=OUI,remove-si-lu=OUI")]
    public void Completion_WithoutPlayedPropagation_WritesTheRawStopPosition_S9fUnchanged_CA5(string tagsCsv)
    {
        var r = new Rig();
        r.Playlist("1", tagsCsv.Split(','));

        r.Engine.Handle("u", "F1", 99 * Min, PositionTrigger.Completion);

        Assert.Equal(99 * Min, r.UserData.GetPosition("v", "F1"));
        Assert.Equal(99 * Min, r.UserData.GetPosition("o", "F1"));
        Assert.Equal(0, r.UserData.MarkPlayedCalls);
    }

    [Theory]
    [InlineData("propager-avancement=NON,propager-lu=OUI")]
    [InlineData("propager-lu=OUI")]
    [InlineData("propager-avancement=NON")]
    public void Completion_WithoutProgressPropagation_WritesNothing(string tagsCsv)
    {
        var r = new Rig();
        r.Playlist("1", tagsCsv.Split(','));
        r.UserData.SetPosition("v", "F1", 95 * Min);
        var writes = r.UserData.SetPositionCalls;

        r.Engine.Handle("u", "F1", 100 * Min, PositionTrigger.Completion);

        Assert.Equal(writes, r.UserData.SetPositionCalls);
        Assert.Equal(95 * Min, r.UserData.GetPosition("v", "F1"));
    }

    [Fact]
    public void Completion_OnAPlaylistWhereTheMemberIsAlreadyAtZero_IsSamePosition_NoWrite_NoPendingEcho()
    {
        var r = new Rig();
        r.Playlist("1", AvLu);

        r.Engine.Handle("u", "F1", 100 * Min, PositionTrigger.Completion);

        Assert.Equal(0, r.UserData.SetPositionCalls);
        Assert.False(r.WriteTracker.TryConsume("v", "F1")); // pas d'écriture => pas d'écho attendu (piège A1)
        Assert.Contains("samePosition=2", r.Positions.Single().Detail);
    }

    [Fact]
    public void Completion_ZeroWrite_RegistersTheAntiEcho_ForEachRealWrite()
    {
        var r = new Rig();
        r.Playlist("1", AvLu);
        r.UserData.SetPosition("v", "F1", 95 * Min);

        r.Engine.Handle("u", "F1", 100 * Min, PositionTrigger.Completion);

        Assert.True(r.WriteTracker.TryConsume("v", "F1"));
        Assert.False(r.WriteTracker.TryConsume("o", "F1")); // « o » était déjà à 0 : rien écrit, rien enregistré
    }

    // ---- Cibles mémorisées : le média peut avoir été retiré avant l'arrêt (cause racine n°2, CA4) ------------------------------

    [Fact]
    public void Completion_AfterTheMediaWasRemovedByRemoveSiLu_StillReachesTheMemorisedPlaylist_CA4()
    {
        var r = new Rig();
        var s = r.Playlist("1", AvLuRm);
        r.UserData.SetPosition("v", "F1", 95 * Min);
        s.Items.Remove("F1"); // le flux du lu a retiré F1 AVANT PlaybackStopped (séquence Emby réelle)

        r.Engine.Handle("u", "F1", 100 * Min, PositionTrigger.Completion, new[] { "1" });

        Assert.Equal(0L, r.UserData.GetPosition("v", "F1"));
        Assert.Single(r.Positions);
    }

    [Fact]
    public void Completion_AfterRemoval_WithoutMemorisedTargets_FindsNothing_ThisIsTheV120Bug()
    {
        // Contre-épreuve (reproduction du bug v1.2.0) : sans cibles mémorisées, la playlist n'est plus trouvée => aucune écriture.
        var r = new Rig();
        var s = r.Playlist("1", AvLuRm);
        r.UserData.SetPosition("v", "F1", 95 * Min);
        s.Items.Remove("F1");

        var result = r.Engine.Handle("u", "F1", 100 * Min, PositionTrigger.Completion);

        Assert.Equal(0, result.Candidates);
        Assert.Equal(95 * Min, r.UserData.GetPosition("v", "F1")); // position périmée : le symptôme rapporté
    }

    [Fact]
    public void Completion_AfterRemoval_WithRawPosition_WhenPlayedPropagationIsOff_StillReachesTheMemorisedPlaylist()
    {
        var r = new Rig();
        var s = r.Playlist("1", Av);
        s.Items.Remove("F1");

        r.Engine.Handle("u", "F1", 99 * Min, PositionTrigger.Completion, new[] { "1" });

        Assert.Equal(99 * Min, r.UserData.GetPosition("v", "F1"));
    }

    [Fact]
    public void Completion_TargetsAreMemorisedUnionCurrent_EachPlaylistHandledExactlyOnce()
    {
        var r = new Rig();
        var s1 = r.Playlist("1", AvLu);   // mémorisée, F1 retiré depuis
        s1.Items.Remove("F1");
        r.Playlist("2", AvLu);            // contient encore F1, non mémorisée (ajoutée en cours de lecture)
        r.Playlist("3", AvLu);            // mémorisée ET contenant encore F1 : ne doit pas être traitée deux fois
        r.UserData.SetPosition("v", "F1", 95 * Min); // le même membre « v » figure dans les trois playlists

        r.Engine.Handle("u", "F1", 100 * Min, PositionTrigger.Completion, new[] { "1", "3" });

        Assert.Equal(new[] { "1", "2", "3" }, r.Positions.Select(e => e.PlaylistId!).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Completion_OnAMemorisedPlaylistThatNoLongerExists_IsIsolated_NoCrash_OthersHandled()
    {
        var r = new Rig();
        r.Playlist("2", AvLu);
        r.UserData.SetPosition("v", "F1", 95 * Min);

        var ex = Record.Exception(() => r.Engine.Handle("u", "F1", 100 * Min, PositionTrigger.Completion, new[] { "gone", "2" }));

        Assert.Null(ex);
        Assert.Equal(0L, r.UserData.GetPosition("v", "F1"));
        Assert.Empty(r.Journal.Of("Error"));
    }

    [Fact]
    public void Completion_TagsAreReadFreshAtTheEnd_AMarkerFlippedDuringPlaybackWins()
    {
        // Couplage uniquement sur la CONFIGURATION de la playlist lue à l'événement (jamais mémorisée) : propager-lu coupé
        // pendant la lecture => position brute, pas 0.
        var r = new Rig();
        var s = r.Playlist("1", AvLu);
        s.Tags = new List<string> { "propager-avancement=OUI", "propager-lu=NON" };

        r.Engine.Handle("u", "F1", 99 * Min, PositionTrigger.Completion, new[] { "1" });

        Assert.Equal(99 * Min, r.UserData.GetPosition("v", "F1"));
    }

    [Fact]
    public void Completion_RespectsR8_MemberWithoutAccessIsSkipped_OtherMembersZeroed()
    {
        var r = new Rig();
        r.Playlist("1", AvLu);
        r.UserData.SetPosition("o", "F1", 95 * Min);
        r.UserData.DenyAccess("v", "F1");

        r.Engine.Handle("u", "F1", 100 * Min, PositionTrigger.Completion);

        Assert.Equal(0L, r.UserData.GetPosition("o", "F1"));
        Assert.Contains("noAccess=1", r.Positions.Single().Detail);
    }

    // ---- Robustesse ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(PositionTrigger.Periodic)]
    [InlineData(PositionTrigger.Pause)]
    [InlineData(PositionTrigger.Stop)]
    [InlineData(PositionTrigger.Completion)]
    public void EveryTrigger_NeverThrows_EvenWhenAWriteFails(PositionTrigger trigger)
    {
        var r = new Rig();
        r.Playlist("1", AvLu);
        r.UserData.SetPosition("v", "F1", 95 * Min);
        r.UserData.ThrowOnSetPositionFor = (uid, _) => uid == "v";

        var ex = Record.Exception(() => r.Engine.Handle("u", "F1", 100 * Min, trigger, new[] { "1" }));

        Assert.Null(ex);
    }
}
