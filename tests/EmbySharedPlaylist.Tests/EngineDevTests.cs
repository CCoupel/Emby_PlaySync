using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Engine;
using EmbySharedPlaylist.Reconciliation;
using Xunit;

namespace EmbySharedPlaylist.Tests;

public class PlayedTransitionTrackerDevTests
{
    private static bool T(PlayedTransitionTracker t, string reason, bool played, string user = "u", string item = "i") =>
        t.OnUserData(user, item, reason, played);

    [Fact]
    public void TogglePlayedTrue_IsACertainTransition_EvenIfTheMemoryAlreadySaysTrue()
    {
        var t = new PlayedTransitionTracker();
        Assert.True(T(t, "TogglePlayed", true));
        Assert.True(T(t, "TogglePlayed", true)); // décocher/recocher sans événement intermédiaire vu : transition certaine
    }

    [Fact]
    public void TogglePlayedFalseThenTrue_IsATransition()
    {
        var t = new PlayedTransitionTracker();
        T(t, "TogglePlayed", true);
        Assert.False(T(t, "TogglePlayed", false));
        Assert.True(T(t, "TogglePlayed", true));
    }

    [Fact]
    public void PlaybackFinished_OnUnplayed_IsATransition_ThenNotAgain()
    {
        var t = new PlayedTransitionTracker();
        Assert.False(T(t, "PlaybackStart", false));
        Assert.False(T(t, "PlaybackProgress", false));
        Assert.True(T(t, "PlaybackFinished", true));
        Assert.False(T(t, "PlaybackFinished", true)); // mémoire = true : pas de redéclenchement
    }

    [Fact]
    public void ReplayOfAnAlreadyPlayedMedia_IsNotATransition_Q1()
    {
        var t = new PlayedTransitionTracker();
        Assert.False(T(t, "PlaybackStart", true));      // le média est déjà lu : mémorisé
        Assert.False(T(t, "PlaybackProgress", true));
        Assert.False(T(t, "PlaybackFinished", true));
    }

    [Fact]
    public void PlayedFalse_IsNeverATransition_AndIsRemembered()
    {
        var t = new PlayedTransitionTracker();
        Assert.False(T(t, "PlaybackProgress", false));
        Assert.False(T(t, "PlaybackFinished", false)); // arrêt à 47 %
        Assert.True(T(t, "PlaybackFinished", true));
    }

    [Theory]
    [InlineData("Import")]
    [InlineData("UpdateUserRating")]
    [InlineData("UpdateHideFromResume")]
    [InlineData(null)]
    [InlineData("MotInconnu")]
    public void NonPlaybackReasons_WithUnknownMemory_RememberWithoutATransition(string? reason)
    {
        var t = new PlayedTransitionTracker();
        Assert.False(T(t, reason!, true));   // A1 : une note/un import sur un média déjà lu ne retire rien
        Assert.False(T(t, reason!, true));
        Assert.False(T(t, "PlaybackFinished", true)); // la mémoire vaut maintenant true : la lecture qui suit n'est pas une transition
    }

    [Theory]
    [InlineData("Import")]
    [InlineData("UpdateUserRating")]
    [InlineData("UpdateHideFromResume")]
    [InlineData("MotInconnu")]
    public void NonPlaybackReasons_AfterAKnownUnplayed_AreARealChange(string reason)
    {
        var t = new PlayedTransitionTracker();
        T(t, "PlaybackStart", false);        // mémoire connue : non lu
        Assert.True(T(t, reason, true));
    }

    [Theory]
    [InlineData("PlaybackProgress")]
    [InlineData("PlaybackFinished")]
    public void PlaybackReasons_WithUnknownMemory_AreATransition(string reason)
    {
        var t = new PlayedTransitionTracker();
        Assert.True(T(t, reason, true));
        Assert.False(T(t, reason, true));
    }

    [Theory]
    [InlineData("Import")]
    [InlineData("UpdateUserRating")]
    [InlineData("UpdateHideFromResume")]
    [InlineData("PlaybackProgress")]
    public void AlreadyPlayedMemory_WithAnyReasonButTogglePlayed_IsNeverATransition(string reason)
    {
        var t = new PlayedTransitionTracker();
        T(t, "PlaybackStart", true);   // mémoire connue : déjà lu
        Assert.False(T(t, reason, true));
    }

    [Fact]
    public void TogglePlayedTrue_StaysCertain_EvenWhenTheMemorySaysAlreadyPlayed()
    {
        var t = new PlayedTransitionTracker();
        T(t, "PlaybackStart", true);                // média déjà lu, mémorisé
        Assert.True(T(t, "TogglePlayed", true));    // geste volontaire : décocher/recocher retire
    }

    [Theory]
    [InlineData("togglePLAYED", true)]
    [InlineData("playbackstart", false)]
    public void Reasons_AreCaseInsensitive(string reason, bool expected)
    {
        var t = new PlayedTransitionTracker();
        Assert.Equal(expected, T(t, reason, true));
    }

    [Fact]
    public void UsersAndItems_DoNotInterfere()
    {
        var t = new PlayedTransitionTracker();
        Assert.True(T(t, "PlaybackFinished", true, "u1", "i1"));
        Assert.True(T(t, "PlaybackFinished", true, "u2", "i1"));
        Assert.True(T(t, "PlaybackFinished", true, "u1", "i2"));
        Assert.False(T(t, "PlaybackFinished", true, "u1", "i1"));
    }

    [Fact]
    public void Capacity_ForgetsTheLeastRecentlyUpdatedPair()
    {
        var t = new PlayedTransitionTracker(2);
        T(t, "PlaybackFinished", true, "u", "1");
        T(t, "PlaybackFinished", true, "u", "2");
        T(t, "PlaybackFinished", true, "u", "1"); // rafraîchit 1 (LRU)
        T(t, "PlaybackFinished", true, "u", "3"); // évince 2
        Assert.Equal(2, t.Count);
        Assert.False(T(t, "PlaybackFinished", true, "u", "1"));   // toujours connu
        Assert.True(T(t, "PlaybackFinished", true, "u", "2"));    // oublié : inconnu = transition
    }

    [Fact]
    public void IsThreadSafe_AndBounded()
    {
        var t = new PlayedTransitionTracker(50);
        var transitions = 0;
        Parallel.For(0, 2000, i =>
        {
            // couples distincts : chaque premier PlaybackFinished(true) d'un couple inconnu est une transition, exactement une fois
            if (t.OnUserData("u", "i" + i, "PlaybackFinished", true)) Interlocked.Increment(ref transitions);
            t.OnUserData("v", "j" + i % 5, i % 2 == 0 ? "PlaybackStart" : "PlaybackFinished", i % 3 == 0);
        });
        Assert.True(t.Count <= 50);
        Assert.Equal(2000, transitions);                      // valeurs cohérentes : aucun premier passage perdu ni doublé
    }

    [Fact]
    public void Constructor_RejectsInvalidCapacity() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlayedTransitionTracker(0));
}

public class ReadRemovalEngineDevTests
{
    private sealed class Rig
    {
        public readonly FakeGateway Gateway = new();
        public readonly SeenPlaylists Seen = new();
        public readonly PlaylistLocks Locks = new();
        public readonly ListJournal Journal = new();
        public bool Suspended;
        public readonly ReadRemovalEngine Engine;

        public Rig()
        {
            var defaults = new DefaultsService(Gateway, Seen, Locks, Journal, "AIDE", () => 2, null, TimeSpan.FromMilliseconds(150), () => Suspended);
            Engine = new ReadRemovalEngine(Gateway, defaults, Seen, Locks, Journal, new FakeClock(), TimeSpan.FromMilliseconds(150), () => Suspended);
        }

        public FakeGateway.State Add(string id, string[] tags, params string[] items)
        {
            var s = Gateway.Add(id, tags);
            s.Overview = "x";
            s.Items = items.ToList();
            s.Members = new List<string> { "o", "u", "v" };
            Seen.TryMarkSeen(id);
            return s;
        }
    }

    [Fact]
    public void ActiveMarker_RemovesTheMedia_AndJournalsMarkerSeenThenRemoval()
    {
        var r = new Rig();
        var s = r.Add("1", new[] { "remove-si-lu=OUI", "propager-lu=NON" }, "m1", "m2");
        var result = r.Engine.Handle("u", "m1");

        Assert.Equal(new[] { "m2" }, s.Items);
        Assert.Equal(new RemovalResult(1, 1, 1, 0, result.DurationMs), result);
        Assert.Equal(new[] { "MarkerSeen", "Removal" }, r.Journal.Entries.Select(e => e.Kind));
        Assert.Equal("family=remove-si-lu state=Oui", r.Journal.Of("MarkerSeen").Single().Detail);
        var removal = r.Journal.Of("Removal").Single();
        Assert.Equal("u", removal.UserId);
        Assert.Equal("m1", removal.ItemId);
        Assert.Equal("1", removal.PlaylistId);
        Assert.StartsWith("entries=1 durationMs=", removal.Detail);
    }

    [Theory]
    [InlineData("remove-si-lu=NON")]
    [InlineData("propager-lu=OUI")]                       // propager-lu n'a aucun effet sur le retrait
    [InlineData("remove-si-lu=OUI", "remove-si-lu=NON")]  // NON l'emporte
    [InlineData("remove-si-lu=oui ", "REMOVE-SI-LU=non")]
    public void InactiveStates_KeepTheMedia_AndJournalSkippedInactive(params string[] tags)
    {
        var r = new Rig();
        var s = r.Add("1", tags, "m1");
        var result = r.Engine.Handle("u", "m1");
        Assert.Equal(new[] { "m1" }, s.Items);
        Assert.Equal(0, r.Gateway.RemoveCalls);
        Assert.Empty(r.Journal.Of("Removal"));
        Assert.Equal(new[] { "inactive" }, r.Journal.Details("Skipped"));
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void NoTagAtAll_OnAnAlreadySeenPlaylist_IsLegacy()
    {
        var r = new Rig();
        var s = r.Add("1", new string[0], "m1");
        r.Engine.Handle("u", "m1");
        Assert.Equal(new[] { "m1" }, s.Items);
        Assert.Equal("family=remove-si-lu state=None", r.Journal.Of("MarkerSeen").Single().Detail);
    }

    [Fact]
    public void Duplicates_AreRemovedOneAtATime()
    {
        var r = new Rig();
        var s = r.Add("1", new[] { "remove-si-lu=OUI" }, "m1", "m2", "m1", "m1");
        var result = r.Engine.Handle("u", "m1");
        Assert.Equal(new[] { "m2" }, s.Items);
        Assert.Equal(3, result.EntriesRemoved);
        Assert.Equal(4, r.Gateway.RemoveCalls); // 3 retraits + 1 appel qui constate l'absence
        Assert.StartsWith("entries=3 ", r.Journal.Of("Removal").Single().Detail);
    }

    [Fact]
    public void RemovalIsCappedAtFiftyEntries()
    {
        var r = new Rig();
        var s = r.Add("1", new[] { "remove-si-lu=OUI" }, Enumerable.Repeat("m1", 60).ToArray());
        var result = r.Engine.Handle("u", "m1");
        Assert.Equal(50, result.EntriesRemoved);
        Assert.Equal(10, s.Items.Count);
    }

    [Fact]
    public void AlreadyRemoved_IsSkipped_NeverBothRemovalAndSkipped()
    {
        var r = new Rig();
        var s = r.Add("1", new[] { "remove-si-lu=OUI" }, "m1");
        // un autre acteur retire le média entre la découverte de la candidate et notre retrait
        r.Gateway.OnRemove = _ => s.Items.Clear();
        var result = r.Engine.Handle("u", "m1");

        Assert.Equal(1, result.Candidates);
        Assert.Equal(0, result.EntriesRemoved);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(r.Journal.Of("Removal"));
        Assert.Equal(new[] { "already-removed" }, r.Journal.Details("Skipped"));
    }

    [Fact]
    public void GlobalBudget_StopsTheProcessing_AndJournalsOneBudgetExceeded()
    {
        var r = new Rig();
        for (var i = 1; i <= 10; i++) r.Add(i.ToString(), new[] { "remove-si-lu=OUI" }, "m1");
        r.Gateway.OnRemove = _ => Thread.Sleep(60);
        var defaults = new DefaultsService(r.Gateway, r.Seen, r.Locks, r.Journal, "A", () => 2, null, TimeSpan.FromSeconds(5));
        var engine = new ReadRemovalEngine(r.Gateway, defaults, r.Seen, r.Locks, r.Journal, new FakeClock(), TimeSpan.FromSeconds(5), null, TimeSpan.FromMilliseconds(150));

        var result = engine.Handle("u", "m1");

        Assert.Equal(10, result.Candidates);
        Assert.InRange(result.PlaylistsChanged, 1, 9);
        Assert.Equal(10 - result.PlaylistsChanged, result.Skipped);
        Assert.Equal(new[] { "budget-exceeded" }, r.Journal.Details("Skipped"));
        Assert.Equal(result.PlaylistsChanged, r.Gateway.Playlists.Values.Count(p => p.Items.Count == 0));
    }

    [Fact]
    public void ZeroBudget_ProcessesNothing_NeverThrows()
    {
        var r = new Rig();
        var s = r.Add("1", new[] { "remove-si-lu=OUI" }, "m1");
        var defaults = new DefaultsService(r.Gateway, r.Seen, r.Locks, r.Journal, "A", () => 2);
        var engine = new ReadRemovalEngine(r.Gateway, defaults, r.Seen, r.Locks, r.Journal, new FakeClock(), null, null, TimeSpan.Zero);
        var result = engine.Handle("u", "m1");
        Assert.Equal(new[] { "m1" }, s.Items);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(new[] { "budget-exceeded" }, r.Journal.Details("Skipped"));
    }

    [Fact]
    public void NonMemberAndPlaylistWithoutTheMedia_AreNotCandidates()
    {
        var r = new Rig();
        r.Add("1", new[] { "remove-si-lu=OUI" }, "m1");
        r.Add("2", new[] { "remove-si-lu=OUI" }, "m9");
        var result = r.Engine.Handle("stranger", "m1");
        Assert.Equal(0, result.Candidates);
        result = r.Engine.Handle("u", "m1");
        Assert.Equal(1, result.Candidates);
        Assert.Equal(new[] { "m9" }, r.Gateway.Playlists["2"].Items);
    }

    [Fact]
    public void ReadOnlyMember_AlsoTriggersTheRemoval_Q3()
    {
        var r = new Rig();
        var s = r.Add("1", new[] { "remove-si-lu=OUI" }, "m1");
        s.Members = new List<string> { "o", "readonly" };
        r.Engine.Handle("readonly", "m1");
        Assert.Empty(s.Items);
    }

    [Fact]
    public void PublicPlaylistWithoutExplicitShare_IsNeverManaged()
    {
        var r = new Rig();
        var s = r.Add("1", new[] { "remove-si-lu=OUI" }, "m1");
        s.Members = new List<string> { "o" };
        r.Engine.Handle("o", "m1");
        Assert.Equal(new[] { "m1" }, s.Items);
    }

    [Fact]
    public void UnseenPlaylist_GetsTheFirstDetectionBeforeTheEvaluation()
    {
        var r = new Rig();
        var s = r.Gateway.Add("1");                     // aucune étiquette, description vide, jamais vue
        s.Members = new List<string> { "o", "u" };
        s.Items = new List<string> { "m1" };
        var result = r.Engine.Handle("u", "m1");

        Assert.Equal(new[] { "remove-si-lu=NON", "propager-lu=NON" }, s.Tags);   // poses de la première détection
        Assert.Equal("AIDE", s.Overview);
        Assert.Equal(new[] { "m1" }, s.Items);                                    // NON posé : rien n'est retiré
        Assert.Equal(new[] { "MarkerPosed", "MarkerPosed", "DescriptionWritten", "MarkerSeen", "Skipped" }, r.Journal.Entries.Select(e => e.Kind));
        Assert.Equal("family=remove-si-lu state=Non", r.Journal.Of("MarkerSeen").Single().Detail);
        Assert.Equal(0, result.EntriesRemoved);
        Assert.True(r.Seen.IsSeen("1"));
    }

    [Fact]
    public void UnseenPlaylistWithOui_IsFirstDetectedThenRemoved()
    {
        var r = new Rig();
        var s = r.Gateway.Add("1", "remove-si-lu=OUI");
        s.Members = new List<string> { "o", "u" };
        s.Items = new List<string> { "m1" };
        r.Engine.Handle("u", "m1");
        Assert.Contains("propager-lu=NON", s.Tags);     // la famille absente est posée
        Assert.Contains("remove-si-lu=OUI", s.Tags);    // l'existante est intacte
        Assert.Empty(s.Items);
    }

    [Fact]
    public void TagsAreReadFreshAtTheEvent_OwnerChangedThemAfterTheDiscovery()
    {
        var r = new Rig();
        var s = r.Add("1", new[] { "remove-si-lu=NON" }, "m1");
        var engine = r.Engine;
        // le propriétaire passe à OUI entre la découverte et l'évaluation : la relecture fraîche le voit
        r.Gateway.OnRemove = null;
        var original = s.Tags;
        s.Tags = new List<string> { "remove-si-lu=OUI" };
        engine.Handle("u", "m1");
        Assert.Empty(s.Items);
        s.Items.Add("m1");
        s.Tags = new List<string> { "remove-si-lu=NON" };
        engine.Handle("u", "m1");
        Assert.Equal(new[] { "m1" }, s.Items);
    }

    [Fact]
    public void AFailingPlaylist_DoesNotStopTheOthers_AndErrorsAreJournaledByTypeOnly()
    {
        var r = new Rig();
        r.Add("1", new[] { "remove-si-lu=OUI" }, "m1");
        var s2 = r.Add("2", new[] { "remove-si-lu=OUI" }, "m1");
        r.Gateway.ThrowOnRemoveFor = id => id == "1";
        var result = r.Engine.Handle("u", "m1");

        Assert.Empty(s2.Items);
        Assert.Equal(2, result.Candidates);
        Assert.Equal(1, result.PlaylistsChanged);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(new[] { "InvalidOperationException" }, r.Journal.Details("Error"));
        Assert.DoesNotContain("secret", string.Join(" ", r.Journal.Entries.Select(e => e.Detail)));
    }

    [Fact]
    public void NeverThrows_EvenWhenTheGatewayCannotListTheCandidates()
    {
        var engine = new ReadRemovalEngine(new ThrowingGateway(), new DefaultsService(new ThrowingGateway(), new SeenPlaylists(), new PlaylistLocks(), new ListJournal(), "A", () => 2),
            new SeenPlaylists(), new PlaylistLocks(), new ListJournal(), new FakeClock());
        var result = Record.Exception(() => engine.Handle("u", "m1"));
        Assert.Null(result);
    }

    [Fact]
    public void LockBusy_IsSkippedWithoutWriting_AndTheNextPlaylistIsStillHandled()
    {
        var r = new Rig();
        var s1 = r.Add("1", new[] { "remove-si-lu=OUI" }, "m1");
        var s2 = r.Add("2", new[] { "remove-si-lu=OUI" }, "m1");
        var held = new ManualResetEventSlim(); var release = new ManualResetEventSlim();
        var t = new Thread(() => { using var l = r.Locks.TryAcquire("1", TimeSpan.FromSeconds(5)); held.Set(); release.Wait(); });
        t.Start(); held.Wait();
        try
        {
            var result = r.Engine.Handle("u", "m1");
            Assert.Equal(new[] { "m1" }, s1.Items);
            Assert.Empty(s2.Items);
            Assert.Contains("lock-busy", r.Journal.Details("Skipped"));
            Assert.Equal(1, result.Skipped);
        }
        finally { release.Set(); t.Join(); }
    }

    [Fact]
    public void OneLockAtATime_AndTheRemovalRunsUnderTheLockInAWriteScope()
    {
        var r = new Rig();
        r.Add("1", new[] { "remove-si-lu=OUI" }, "m1");
        r.Add("2", new[] { "remove-si-lu=OUI" }, "m1");
        var held = new List<string>();
        r.Gateway.OnRemove = id =>
        {
            Assert.True(WriteScope.Active);
            Assert.True(r.Locks.IsHeldByCurrentThread(id));
            lock (held) held.Add(id);
        };
        r.Engine.Handle("u", "m1");
        Assert.Contains("1", held);
        Assert.Contains("2", held);
        Assert.False(r.Locks.IsHeldByCurrentThread("1") || r.Locks.IsHeldByCurrentThread("2"));
        Assert.False(WriteScope.Active);
    }

    [Fact]
    public void Suspended_DoesNothing_NoGatewayCall()
    {
        var r = new Rig();
        var s = r.Add("1", new[] { "remove-si-lu=OUI" }, "m1");
        r.Suspended = true;
        var result = r.Engine.Handle("u", "m1");
        Assert.Equal(new RemovalResult(0, 0, 0, 0, 0), result);
        Assert.Equal(new[] { "m1" }, s.Items);
        Assert.Equal(0, r.Gateway.RemoveCalls);
        Assert.Empty(r.Journal.Entries);
    }

    [Fact]
    public void ConcurrentTransitionsOnTheSameMedia_RemoveEachEntryExactlyOnce()
    {
        var r = new Rig();
        var engine = new ReadRemovalEngine(r.Gateway, new DefaultsService(r.Gateway, r.Seen, r.Locks, r.Journal, "A", () => 2, null, TimeSpan.FromSeconds(5)),
            r.Seen, r.Locks, r.Journal, new FakeClock(), TimeSpan.FromSeconds(5));
        var s = r.Add("1", new[] { "remove-si-lu=OUI" }, "m1", "m1", "m2");
        Parallel.For(0, 8, _ => engine.Handle("u", "m1"));
        Assert.Equal(new[] { "m2" }, s.Items);
        Assert.Equal(2, r.Journal.Of("Removal").Sum(e => int.Parse(e.Detail!.Split(' ')[0].Split('=')[1])));
    }

    [Fact]
    public void TheReadFlagIsNeverTouched()
    {
        // Le moteur n'a aucun accès aux données de lecture : la passerelle ne sait que retirer et poser (R9).
        var methods = typeof(IPlaylistGateway).GetMethods().Select(m => m.Name).ToList();
        Assert.DoesNotContain(methods, n => n.Contains("Played", StringComparison.OrdinalIgnoreCase) || n.Contains("UserData", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class ThrowingGateway : IPlaylistGateway
    {
        public IReadOnlyList<PlaylistSnapshot> ListSharedPlaylists() => throw new InvalidOperationException("secret");
        public IReadOnlyList<PlaylistSnapshot> ListSharedPlaylistsOfUserContaining(string userId, string itemId) => throw new InvalidOperationException("secret");
        public PlaylistSnapshot? Get(string playlistId) => throw new InvalidOperationException("secret");
        public bool RemoveOneEntry(string playlistId, string itemId) => throw new InvalidOperationException("secret");
        public ApplyResult ApplyDefaults(string playlistId, IReadOnlyList<Marker.MarkerFamily> familiesToPose, string? overviewIfEmpty) => throw new InvalidOperationException("secret");
    }
}

public class HandlerStatsDevTests
{
    [Fact]
    public void Record_TracksCountLastAndMax()
    {
        var h = new HandlerStats();
        Assert.Equal((0L, 0L, 0L), h.Snapshot());
        h.Record(10); h.Record(50); h.Record(20);
        Assert.Equal((3L, 20L, 50L), h.Snapshot());
    }

    [Fact]
    public void Record_IsThreadSafe()
    {
        var h = new HandlerStats();
        Parallel.For(0, 1000, i => h.Record(i));
        var s = h.Snapshot();
        Assert.Equal(1000, s.Count);
        Assert.Equal(999, s.MaxMs);
    }
}
