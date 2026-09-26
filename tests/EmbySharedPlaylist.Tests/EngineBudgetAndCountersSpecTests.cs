using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Engine;
using EmbySharedPlaylist.Reconciliation;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// Spécification du budget global du gestionnaire (Skipped <c>budget-exceeded</c>) : le gestionnaire tourne sur le fil de l'événement
/// et ne doit pas s'éterniser ; le retrait est idempotent, la transition suivante reprend. Le Skipped <c>no-effect</c> est produit par
/// l'adaptateur SDK (EmbyPlaylistGateway, repli RemoveListItemsByItemIds relu après coup) : non testable sans SDK, couvert en QUALIF.
/// </summary>
public class ReadRemovalEngineBudgetSpecTests
{
    private sealed class Rig
    {
        public readonly FakeGateway Gateway = new();
        public readonly SeenPlaylists Seen = new();
        public readonly PlaylistLocks Locks = new();
        public readonly ListJournal Journal = new();
        public readonly FakeClock Clock = new();
        public readonly DefaultsService Defaults;

        public Rig() => Defaults = new DefaultsService(Gateway, Seen, Locks, Journal, "AIDE", 2);

        public ReadRemovalEngine Engine(TimeSpan? budget) =>
            new(Gateway, Defaults, Seen, Locks, Journal, Clock, TimeSpan.FromMilliseconds(500), null, budget);

        public FakeGateway.State Playlist(string id, params string[] items)
        {
            var s = Gateway.Add(id, "remove-si-lu=OUI");
            s.Overview = "déjà";
            s.Members = new List<string> { "o", "m" };
            s.Items = items.ToList();
            Seen.TryMarkSeen(id);
            return s;
        }
    }

    [Fact]
    public void ZeroBudget_SkipsTheCandidates_WritesNothing_AndJournalsBudgetExceeded()
    {
        var r = new Rig();
        var a = r.Playlist("a", "x");
        var b = r.Playlist("b", "x");

        var result = r.Engine(TimeSpan.Zero).Handle("m", "x");

        Assert.Equal(2, result.Candidates);
        Assert.Equal(2, result.Skipped);
        Assert.Equal(0, result.EntriesRemoved);
        Assert.Equal(new[] { "x" }, a.Items);
        Assert.Equal(new[] { "x" }, b.Items);
        Assert.Equal(0, r.Gateway.RemoveCalls);
        Assert.Contains("budget-exceeded", r.Journal.Details("Skipped"));
    }

    [Fact]
    public void ABudgetExhaustedByTheFirstPlaylist_StopsBeforeTheNext_AndTheNextTransitionResumes()
    {
        var r = new Rig();
        var a = r.Playlist("a", "x", "x");
        var b = r.Playlist("b", "x");
        r.Gateway.OnRemove = id => { if (id == "a") Thread.Sleep(400); };     // un retrait dépasse à lui seul le budget de 300 ms

        var first = r.Engine(TimeSpan.FromMilliseconds(300)).Handle("m", "x");

        Assert.Equal(2, first.Candidates);
        Assert.Equal(1, first.EntriesRemoved);                                  // la boucle de « a » s'arrête au budget : une seule entrée
        Assert.Equal(new[] { "x" }, a.Items);
        Assert.Equal(new[] { "x" }, b.Items);                                   // « b » n'a pas été traitée
        Assert.Contains("budget-exceeded", r.Journal.Details("Skipped"));
        Assert.Equal(1, r.Journal.Of("Removal").Count());                       // le retrait partiel est journalisé une fois

        // reprise : une transition suivante, avec le budget par défaut, termine le travail (retrait idempotent)
        r.Gateway.OnRemove = null;
        var second = r.Engine(null).Handle("m", "x");

        Assert.Empty(a.Items);
        Assert.Empty(b.Items);
        Assert.Equal(2, second.PlaylistsChanged);
    }

    [Fact]
    public void ADefaultBudget_IsGenerousEnoughForANormalTransition()
    {
        var r = new Rig();
        var a = r.Playlist("a", "x", "y");
        var result = r.Engine(null).Handle("m", "x");
        Assert.Equal(1, result.EntriesRemoved);
        Assert.Equal(new[] { "y" }, a.Items);
        Assert.DoesNotContain("budget-exceeded", r.Journal.Details("Skipped"));
        Assert.True(ReadRemovalEngine.DefaultBudget >= TimeSpan.FromSeconds(5));
    }
}

/// <summary>
/// Spécification des compteurs de Skipped (exposés par Diagnostics/State, champ SkippedCounts) : tous les Skipped sont comptés par raison ;
/// already-seen, reentrant, not-shared et unknown-owner ne vont plus au journal (compteurs seulement) ; les autres restent des entrées.
/// « Un écho par écriture » du plugin = au plus +1 already-seen ou reentrant par écriture.
/// </summary>
public class SkippedCountersSpecTests
{
    private static JournalEntry Skipped(string reason) => JournalEntries.SkippedEntry(null, "p", reason);

    [Theory]
    [InlineData("already-seen")]
    [InlineData("reentrant")]
    [InlineData("not-shared")]
    [InlineData("unknown-owner")]
    public void NoisyReasons_AreCounted_ButNeverJournaled(string reason)
    {
        var inner = new ListJournal();
        var counters = new SkippedCounters();
        var journal = new AggregatingJournal(inner, counters);

        for (var i = 0; i < 3; i++) journal.Add(Skipped(reason));

        Assert.Equal(3L, counters.Snapshot()[reason]);
        Assert.Empty(inner.Entries);
    }

    [Theory]
    [InlineData("lock-busy")]
    [InlineData("inactive")]
    [InlineData("already-removed")]
    [InlineData("marker-present")]
    [InlineData("suspended")]
    [InlineData("no-effect")]
    [InlineData("budget-exceeded")]
    public void OtherReasons_AreCounted_AndStayInTheJournal(string reason)
    {
        var inner = new ListJournal();
        var counters = new SkippedCounters();
        var journal = new AggregatingJournal(inner, counters);

        journal.Add(Skipped(reason));

        Assert.Equal(1L, counters.Snapshot()[reason]);
        Assert.Equal(new[] { reason }, inner.Details("Skipped"));
    }

    [Fact]
    public void OtherKinds_PassThrough_AndAreNotCounted()
    {
        var inner = new ListJournal();
        var counters = new SkippedCounters();
        var journal = new AggregatingJournal(inner, counters);

        journal.Add(JournalEntries.Of(null, JournalEntries.MarkerPosed, "p", "family=remove-si-lu cause=first-detection"));
        journal.Add(JournalEntries.Of(null, "Removal", "p", "entries=1 durationMs=3"));
        journal.Add(JournalEntries.ErrorEntry(null, "p", new InvalidOperationException("secret")));

        Assert.Equal(3, inner.Entries.Count);
        Assert.Empty(counters.Snapshot());
    }

    [Fact]
    public void ANullOrEmptyReason_IsCountedAsUnknown()
    {
        var counters = new SkippedCounters();
        var journal = new AggregatingJournal(new ListJournal(), counters);

        journal.Add(new JournalEntry { Kind = JournalEntries.Skipped, Detail = null });
        journal.Add(new JournalEntry { Kind = JournalEntries.Skipped, Detail = string.Empty });

        Assert.Equal(2L, counters.Snapshot()["unknown"]);
    }

    [Fact]
    public void Snapshot_IsACopy()
    {
        var counters = new SkippedCounters();
        counters.Increment("inactive");
        var copy = counters.Snapshot();
        counters.Increment("inactive");
        Assert.Equal(1L, copy["inactive"]);
        Assert.Equal(2L, counters.Snapshot()["inactive"]);
    }

    [Fact]
    public void Counting_IsThreadSafe()
    {
        var counters = new SkippedCounters();
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++) counters.Increment("already-seen");
        })).ToArray();
        Task.WaitAll(tasks);
        Assert.Equal(8000L, counters.Snapshot()["already-seen"]);
    }

    [Fact]
    public void OneEchoPerPluginWrite_FirstDetectionThenItsEcho_AreCountedNotJournaled()
    {
        // Chaîne réelle : écriture de pose (première détection) -> l'écho de cette écriture arrive pendant le WriteScope (reentrant),
        // puis, hors scope, comme événement d'une playlist déjà vue (already-seen). Aucun ne doit alourdir le journal.
        var gateway = new FakeGateway();
        var s = gateway.Add("p");
        s.Members = new List<string> { "o", "m" };
        var seen = new SeenPlaylists();
        var locks = new PlaylistLocks();
        var inner = new ListJournal();
        var counters = new SkippedCounters();
        var journal = new AggregatingJournal(inner, counters);
        var clock = new FakeClock();
        var defaults = new DefaultsService(gateway, seen, locks, journal, "AIDE", () => 2, clock);
        var coordinator = new FirstDetectionCoordinator(gateway, defaults, seen, journal, clock);

        coordinator.OnPlaylistEvent("p");                                        // première détection : UNE écriture (ApplyDefaults)
        Assert.Equal(1, gateway.ApplyCalls);
        using (EmbySharedPlaylist.Core.WriteScope.Enter()) coordinator.OnPlaylistEvent("p");   // écho pendant une écriture du plugin
        coordinator.OnPlaylistEvent("p");                                        // écho après l'écriture

        var snapshot = counters.Snapshot();
        Assert.Equal(1L, snapshot["reentrant"]);
        Assert.Equal(1L, snapshot["already-seen"]);
        Assert.Equal(1, gateway.ApplyCalls);                                     // aucune seconde écriture
        Assert.DoesNotContain(inner.Entries, e => e.Kind == JournalEntries.Skipped);
    }
}
