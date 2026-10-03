using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Engine;
using EmbySharedPlaylist.Marker;
using EmbySharedPlaylist.Reconciliation;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// F3 (v1.2.2.1, #59) — la boucle de retrait du moteur : <c>count</c> (donc <c>Removal entries=N</c>) ne compte QUE les suppressions ayant
/// effectivement baissé le compte de la cible (<see cref="RemoveOutcome.Removed"/>) ; 2 tentatives au plus sur <see cref="RemoveOutcome.NoEffect"/> ;
/// arrêt dès <c>TargetRemaining == 0</c> (aucun appel de vérification en fin de boucle). Le journal <c>Skipped stale-entry</c> est écrit par la
/// passerelle Emby (couche Emby, non testable hors Emby) : le moteur n'en écrit pas.
/// </summary>
public class ReadRemovalEngineF3SpecTests
{
    /// <summary>Passerelle scriptée : délègue tout à FakeGateway sauf RemoveEntry, qui rejoue une séquence de résultats.</summary>
    private sealed class Scripted : IPlaylistGateway
    {
        public readonly FakeGateway Inner = new();
        public readonly Queue<RemoveResult> Script = new();
        public int RemoveEntryCalls;
        public IReadOnlyList<PlaylistSnapshot> ListSharedPlaylists() => Inner.ListSharedPlaylists();
        public IReadOnlyList<PlaylistSnapshot> ListSharedPlaylistsOfUserContaining(string u, string i) => Inner.ListSharedPlaylistsOfUserContaining(u, i);
        public PlaylistSnapshot? Get(string id) => Inner.Get(id);
        public bool RemoveOneEntry(string playlistId, string itemId) => throw new InvalidOperationException("le moteur F3 appelle RemoveEntry");
        public RemoveResult RemoveEntry(string playlistId, string itemId)
        {
            RemoveEntryCalls++;
            var r = Script.Dequeue();
            if (r.Outcome == RemoveOutcome.Removed) Inner.Playlists[playlistId].Items.Remove(itemId);   // état réel de la playlist suit
            return r;
        }
        public ApplyResult ApplyDefaults(string p, IReadOnlyList<MarkerFamily> f, OverviewChange? o) => Inner.ApplyDefaults(p, f, o);
        public ReplaceFamilyResult ReplaceFamily(string p, MarkerFamily f, bool e) => Inner.ReplaceFamily(p, f, e);
        public string CreatePlaylist(string o, string n) => Inner.CreatePlaylist(o, n);
    }

    private static (ReadRemovalEngine Engine, Scripted Gw, ListJournal Journal) Build(params string[] items)
    {
        var gw = new Scripted();
        var seen = new SeenPlaylists(); var locks = new PlaylistLocks(); var journal = new ListJournal(); var clock = new FakeClock();
        var timeout = TimeSpan.FromMilliseconds(500);
        var defaults = new DefaultsService(gw, seen, locks, journal, "AIDE", () => 2, clock, timeout);
        var engine = new ReadRemovalEngine(gw, new FakeUserDataGateway(), new PluginWriteTracker(), defaults, seen, locks, journal, clock, timeout);
        var s = gw.Inner.Add("p", "remove-si-lu=OUI", "propager-lu=OUI");
        s.Overview = "déjà"; s.Members = new List<string> { "o", "m", "r" }; s.Items = items.ToList();
        seen.TryMarkSeen("p");
        return (engine, gw, journal);
    }

    private static RemoveResult Removed(int remaining) => new(RemoveOutcome.Removed, remaining);
    private static RemoveResult NoEffect() => new(RemoveOutcome.NoEffect, 1);

    [Fact]
    public void NoEffectThenRemoved_CountsOneEntry_AndStopsWithoutAVerificationCall()
    {
        var (engine, gw, journal) = Build("x", "y");
        gw.Script.Enqueue(NoEffect());          // identifiant périmé : aucune suppression
        gw.Script.Enqueue(Removed(0));          // retentative : la cible disparaît

        var result = engine.Handle("m", "x");

        Assert.Equal(1, result!.EntriesRemoved);                       // PAS 2 : la suppression sans effet n'est pas un retrait
        Assert.Equal(2, gw.RemoveEntryCalls);                          // arrêt à TargetRemaining == 0 : pas de 3e appel de vérification
        var removal = Assert.Single(journal.Of("Removal"));
        Assert.StartsWith("entries=1 durationMs=", removal.Detail);
        Assert.Empty(journal.Of("Error"));
    }

    [Fact]
    public void TwoNoEffects_StopTheLoop_AtTwoAttempts_NoInfiniteLoop_AlreadyRemovedJournaled()
    {
        var (engine, gw, journal) = Build("x", "y");
        gw.Script.Enqueue(NoEffect());
        gw.Script.Enqueue(NoEffect());

        var result = engine.Handle("m", "x");

        Assert.Equal(0, result!.EntriesRemoved);
        Assert.Equal(ReadRemovalEngine.MaxNoEffectAttempts, gw.RemoveEntryCalls);
        Assert.Equal(2, gw.RemoveEntryCalls);
        Assert.Empty(journal.Of("Removal"));
        Assert.Contains(journal.Entries, e => e.Kind == "Skipped" && e.Detail != null && e.Detail.Contains("already-removed"));
    }

    [Fact]
    public void DuplicatesRemovedOneAtATime_EntriesCountsEachRealRemoval_StopsAtZeroRemaining()
    {
        var (engine, gw, journal) = Build("x", "y", "x", "x");
        gw.Script.Enqueue(Removed(2));
        gw.Script.Enqueue(Removed(1));
        gw.Script.Enqueue(Removed(0));

        var result = engine.Handle("m", "x");

        Assert.Equal(3, result!.EntriesRemoved);
        Assert.Equal(3, gw.RemoveEntryCalls);                          // pas d'appel de vérification après TargetRemaining == 0
        Assert.StartsWith("entries=3 durationMs=", Assert.Single(journal.Of("Removal")).Detail);
    }

    [Fact]
    public void RemovedWithUnknownRemaining_KeepsLooping_UntilNotFound()
    {
        var (engine, gw, _) = Build("x");
        gw.Script.Enqueue(new RemoveResult(RemoveOutcome.Removed, -1));    // reste inconnu : la boucle rappelle
        gw.Script.Enqueue(new RemoveResult(RemoveOutcome.NotFound));

        var result = engine.Handle("m", "x");

        Assert.Equal(1, result!.EntriesRemoved);
        Assert.Equal(2, gw.RemoveEntryCalls);
    }

    [Fact]
    public void NoEffectCountsAreIndependentOfRemoved_ARemovedBetweenDoesNotResetBelowTheLimit_OnlyTwoNoEffectsStop()
    {
        var (engine, gw, _) = Build("x", "x");
        gw.Script.Enqueue(Removed(1));
        gw.Script.Enqueue(NoEffect());
        gw.Script.Enqueue(Removed(0));

        var result = engine.Handle("m", "x");

        Assert.Equal(2, result!.EntriesRemoved);
        Assert.Equal(3, gw.RemoveEntryCalls);
    }
}
