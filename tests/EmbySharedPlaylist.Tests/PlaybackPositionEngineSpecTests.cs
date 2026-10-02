using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Engine;
using EmbySharedPlaylist.Reconciliation;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// Tests de SPÉCIFICATION de <see cref="PlaybackPositionEngine"/> (#45), complémentaires de
/// <see cref="PlaybackPositionEngineDevTests"/> (matrice des marqueurs, R8, same-position, A1, non-transitivité) : ce
/// fichier couvre le BUDGET propre à ce moteur — <c>PlaybackPositionEngineDevTests.OneLockAtATime_AndTheWriteRunsUnderTheLock</c>
/// note explicitement que le budget « est couvert par les tests de ReadRemovalEngine », mais <c>_budget</c> est un champ
/// propre à CETTE instance/boucle (copie de comportement, pas de code partagé) : mérite sa propre vérification, comme
/// <see cref="ReadRemovalEngineBudgetSpecTests"/> pour le retrait/la propagation du lu.
///
/// Chaîne complète (déclencheur ISessionManager -> PlaybackSyncTracker/Stopped -> seuil 30 s -> PlaybackPositionEngine ;
/// depuis v1.2.0 (#57, D21) la garde D-c « déclencheur déjà lu » est SUPPRIMÉE et la famille est propager-avancement,
/// pendant de <see cref="PropagationChainSpecTests"/> pour #20) : Emby/PlaybackSessionListener (livré, #45/#46) est un
/// adaptateur SDK fin, comme EmbyPlaylistGateway/EmbyUserDataGateway — sa logique (TryExtract, seuil) est en méthodes
/// statiques privées couplées aux types de session Emby réels (PlaybackProgressEventArgs.Session/.Item) et à l'état
/// statique PluginRuntime, PAS une classe injectable comme PlaybackEventProcessor pour #20/#21 : pas de point d'injection
/// pour un test de chaîne en mémoire, contrairement à PropagationChainSpecTests. Non testable ici sans SDK, comme les
/// autres adaptateurs Emby ; couvert par tests/integration/22-avancement.sh (I27-I36 ; I32/I37 réécrits v1.2.0 : relecture d'un média lu propagée, #57) en QUALIF.
/// </summary>
public class PlaybackPositionEngineSpecTests
{
    private sealed class Rig
    {
        public readonly FakeGateway Gateway = new();
        public readonly FakeUserDataGateway UserData = new();
        public readonly PluginWriteTracker WriteTracker = new();
        public readonly SeenPlaylists Seen = new();
        public readonly PlaylistLocks Locks = new();
        public readonly ListJournal Journal = new();
        public readonly FakeClock Clock = new();
        public readonly DefaultsService Defaults;

        public Rig() => Defaults = new DefaultsService(Gateway, Seen, Locks, Journal, "AIDE", 2);

        public PlaybackPositionEngine Engine(IUserDataGateway? userData, TimeSpan? budget) =>
            new(Gateway, userData ?? UserData, WriteTracker, Defaults, Seen, Locks, Journal, Clock, TimeSpan.FromMilliseconds(500), budget);

        public FakeGateway.State Playlist(string id, params string[] members)
        {
            var s = Gateway.Add(id, "propager-avancement=OUI");
            s.Overview = "déjà";
            s.Members = members.ToList();
            s.Items = new List<string> { "m1" };
            Seen.TryMarkSeen(id);
            return s;
        }
    }

    /// <summary>Décore <see cref="IUserDataGateway"/> pour ralentir artificiellement chaque membre lu (ici : GetPosition, appelé
    /// pour chaque membre AVANT l'écriture, comme dans EngineDevTests.SlowUserDataGateway pour ReadRemovalEngine).</summary>
    private sealed class SlowUserDataGateway : IUserDataGateway
    {
        private readonly IUserDataGateway _inner;
        private readonly Action _onCall;
        public SlowUserDataGateway(IUserDataGateway inner, Action onCall) { _inner = inner; _onCall = onCall; }
        public bool HasAccess(string userId, string itemId) => _inner.HasAccess(userId, itemId);
        public bool? IsPlayed(string userId, string itemId) => _inner.IsPlayed(userId, itemId);
        public bool MarkPlayed(string userId, string itemId) => _inner.MarkPlayed(userId, itemId);
        public long? GetPosition(string userId, string itemId) { _onCall(); return _inner.GetPosition(userId, itemId); }
        public bool SetPosition(string userId, string itemId, long ticks) => _inner.SetPosition(userId, itemId, ticks);
    }

    [Fact]
    public void ZeroBudget_SkipsAllCandidatePlaylists_WritesNothing_AndJournalsBudgetExceeded()
    {
        var r = new Rig();
        r.Playlist("a", "o", "u", "v");
        r.Playlist("b", "o", "u", "v");

        var result = r.Engine(null, TimeSpan.Zero).Handle("u", "m1", 12345);

        Assert.Equal(2, result.Candidates);
        Assert.Equal(2, result.Skipped);
        Assert.Equal(0, result.PlaylistsChanged);
        Assert.Equal(0, result.Propagated);
        Assert.Equal(0, r.UserData.SetPositionCalls);
        Assert.Contains("budget-exceeded", r.Journal.Details("Skipped"));
    }

    [Fact]
    public void ADefaultBudget_IsGenerousEnoughForANormalPropagation()
    {
        var r = new Rig();
        r.Playlist("a", "o", "u", "v");
        var result = r.Engine(null, null).Handle("u", "m1", 500);
        Assert.Equal(2, result.Propagated); // o + v
        Assert.DoesNotContain("budget-exceeded", r.Journal.Details("Skipped"));
    }

    [Fact]
    public void BudgetExhaustedMidMembers_StopsThatPlaylistEarly_RemainingMembersUntouched_NoWriteAndNoErrorForThem()
    {
        var r = new Rig();
        r.Playlist("a", "o", "u", "u1", "u2", "u3", "u4", "u5"); // noms distincts de l'itemId "m1" (Rig.Playlist), pour éviter toute confusion
        var calls = 0;
        var slow = new SlowUserDataGateway(r.UserData, () => { calls++; if (calls > 1) Thread.Sleep(150); });

        r.Engine(slow, TimeSpan.FromMilliseconds(120)).Handle("u", "m1", 999);

        var entry = r.Journal.Of("PositionPropagation").Single();
        var parts = entry.Detail!.Split(' ').Select(p => p.Split('=')).Where(p => int.TryParse(p[1], out _)).ToDictionary(p => p[0], p => int.Parse(p[1]));
        // Le budget global est partagé entre tous les membres de la playlist : certains n'ont pas été atteints, sans
        // qu'aucune entrée Skipped ne soit journalisée POUR EUX (contrairement à no-access/same-position, qui sont
        // évalués) — juste silencieusement non traités, reprise implicite au prochain événement (aucune erreur).
        Assert.True(parts["propagated"] + parts["samePosition"] + parts["noAccess"] < parts["members"]);
        Assert.Empty(r.Journal.Of("Error"));
    }

    [Fact]
    public void ABudgetExhaustedByOnePlaylist_LeavesTheNextPlaylistEntirelyUnprocessed()
    {
        var r = new Rig();
        r.Playlist("a", "o", "u", "v");
        r.Playlist("b", "o", "u", "v");
        var calls = 0;
        var slow = new SlowUserDataGateway(r.UserData, () => { calls++; if (calls == 1) Thread.Sleep(200); });

        var result = r.Engine(slow, TimeSpan.FromMilliseconds(100)).Handle("u", "m1", 1);

        Assert.Equal(2, result.Candidates);
        Assert.Equal(1, result.Skipped); // "b" jamais atteinte : budget global épuisé au retour de "a"
        Assert.Contains("budget-exceeded", r.Journal.Details("Skipped"));
        Assert.Empty(r.Journal.Of("PositionPropagation").Where(e => e.PlaylistId == "b"));
    }
}
