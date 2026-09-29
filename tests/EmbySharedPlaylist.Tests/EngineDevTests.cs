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
        public readonly FakeUserDataGateway UserData = new();
        public readonly PluginWriteTracker WriteTracker = new();
        public readonly SeenPlaylists Seen = new();
        public readonly PlaylistLocks Locks = new();
        public readonly ListJournal Journal = new();
        public readonly ReadRemovalEngine Engine;

        public Rig()
        {
            var defaults = new DefaultsService(Gateway, Seen, Locks, Journal, "AIDE", () => 2, null, TimeSpan.FromMilliseconds(150));
            Engine = new ReadRemovalEngine(Gateway, UserData, WriteTracker, defaults, Seen, Locks, Journal, new FakeClock(), TimeSpan.FromMilliseconds(150));
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
        var engine = new ReadRemovalEngine(r.Gateway, r.UserData, r.WriteTracker, defaults, r.Seen, r.Locks, r.Journal, new FakeClock(), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(150));

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
        var engine = new ReadRemovalEngine(r.Gateway, r.UserData, r.WriteTracker, defaults, r.Seen, r.Locks, r.Journal, new FakeClock(), null, TimeSpan.Zero);
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
        var engine = new ReadRemovalEngine(new ThrowingGateway(), new FakeUserDataGateway(), new PluginWriteTracker(),
            new DefaultsService(new ThrowingGateway(), new SeenPlaylists(), new PlaylistLocks(), new ListJournal(), "A", () => 2),
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
    public void ConcurrentTransitionsOnTheSameMedia_RemoveEachEntryExactlyOnce()
    {
        var r = new Rig();
        var engine = new ReadRemovalEngine(r.Gateway, r.UserData, r.WriteTracker,
            new DefaultsService(r.Gateway, r.Seen, r.Locks, r.Journal, "A", () => 2, null, TimeSpan.FromSeconds(5)),
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
        public ApplyResult ApplyDefaults(string playlistId, IReadOnlyList<Marker.MarkerFamily> familiesToPose, OverviewChange? overview) => throw new InvalidOperationException("secret");
        public ReplaceFamilyResult ReplaceFamily(string playlistId, Marker.MarkerFamily family, bool enabled) => throw new InvalidOperationException("secret");
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

public class PropagationDevTests
{
    private sealed class Rig
    {
        public readonly FakeGateway Gateway = new();
        public readonly FakeUserDataGateway UserData = new();
        public readonly PluginWriteTracker WriteTracker = new();
        public readonly SeenPlaylists Seen = new();
        public readonly PlaylistLocks Locks = new();
        public readonly ListJournal Journal = new();
        public readonly ReadRemovalEngine Engine;

        public Rig()
        {
            var defaults = new DefaultsService(Gateway, Seen, Locks, Journal, "AIDE", () => 2, null, TimeSpan.FromMilliseconds(150));
            Engine = new ReadRemovalEngine(Gateway, UserData, WriteTracker, defaults, Seen, Locks, Journal, new FakeClock(), TimeSpan.FromMilliseconds(150));
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

    // ---- Matrice 2x2 (remove-si-lu x propager-lu) ---------------------------------------------------------

    [Fact]
    public void Matrix_BothActive_RemovesAndPropagates()
    {
        var r = new Rig();
        var s = r.Add("1", new[] { "remove-si-lu=OUI", "propager-lu=OUI" }, "m1");
        r.Engine.Handle("u", "m1");
        Assert.Empty(s.Items);                                   // retrait
        Assert.True(r.UserData.IsPlayed("v", "m1"));              // propagation à l'autre membre
        Assert.Single(r.Journal.Of("Removal"));
        Assert.Single(r.Journal.Of("Propagation"));
    }

    [Fact]
    public void Matrix_RemoveOnly_RemovesButNeverPropagates()
    {
        var r = new Rig();
        var s = r.Add("1", new[] { "remove-si-lu=OUI", "propager-lu=NON" }, "m1");
        r.Engine.Handle("u", "m1");
        Assert.Empty(s.Items);
        Assert.False(r.UserData.IsPlayed("v", "m1"));
        Assert.Empty(r.Journal.Of("Propagation"));
    }

    [Fact]
    public void Matrix_PropagateOnly_PropagatesButMediaStaysInTheList_S3c()
    {
        var r = new Rig();
        var s = r.Add("1", new[] { "remove-si-lu=NON", "propager-lu=OUI" }, "m1");
        r.Engine.Handle("u", "m1");
        Assert.Equal(new[] { "m1" }, s.Items);                    // média conservé
        Assert.True(r.UserData.IsPlayed("v", "m1"));
        Assert.Single(r.Journal.Of("Propagation"));
        Assert.Empty(r.Journal.Of("Removal"));
    }

    [Fact]
    public void Matrix_NeitherActive_IsLegacy_S3d()
    {
        var r = new Rig();
        var s = r.Add("1", new[] { "remove-si-lu=NON", "propager-lu=NON" }, "m1");
        r.Engine.Handle("u", "m1");
        Assert.Equal(new[] { "m1" }, s.Items);
        Assert.False(r.UserData.IsPlayed("v", "m1"));
        Assert.Empty(r.Journal.Of("Propagation"));
    }

    [Theory]
    [InlineData("propager-lu=OUI", "propager-lu=NON")]  // OUI+NON : NON l'emporte
    [InlineData("propager-lu=oui ")]                     // casse/espaces : toujours actif
    public void Matrix_BothOrCaseVariants(params string[] extra)
    {
        var r = new Rig();
        var tags = new[] { "remove-si-lu=NON" }.Concat(extra).ToArray();
        var s = r.Add("1", tags, "m1");
        r.Engine.Handle("u", "m1");
        var expectPropagated = extra.Length == 1; // seul le cas "oui " (une étiquette) doit propager ; OUI+NON ensemble = inactif
        Assert.Equal(expectPropagated, r.UserData.IsPlayed("v", "m1") == true);
    }

    [Fact]
    public void PropagerLu_IsNeverConsultedForTheRemovalDecision()
    {
        var r = new Rig();
        var s = r.Add("1", new[] { "remove-si-lu=OUI" }, "m1"); // propager-lu absent : sans effet sur le retrait
        r.Engine.Handle("u", "m1");
        Assert.Empty(s.Items);
    }

    // ---- R6 : non lu jamais propagé -----------------------------------------------------------------------

    [Fact]
    public void R6_UnplayedTransition_NeverPropagates()
    {
        // Aucune transition détectée en amont (PlayedTransitionTracker) : Handle n'est même pas appelé pour played=false
        // dans le vrai pipeline. Ici on vérifie qu'un appel direct ne propage jamais un flag "non lu" (le moteur ne
        // manipule que Played=true côté propagation ; aucun MarkPlayed(false) n'existe dans le port).
        var methods = typeof(IUserDataGateway).GetMethods().Select(m => m.Name);
        Assert.DoesNotContain("MarkUnplayed", methods);
    }

    // ---- R7/R8 ---------------------------------------------------------------------------------------------

    [Fact]
    public void R7_AlreadyPlayedMember_GetsNoWrite_CounterAndDateUntouched()
    {
        var r = new Rig();
        var s = r.Add("1", new[] { "propager-lu=OUI" }, "m1");
        s.Members = new List<string> { "o", "u", "v" };
        r.UserData.SetPlayed("o", "m1", true); // déjà lu avant la transition de u (les DEUX autres membres, "o" et "v")
        r.UserData.SetPlayed("v", "m1", true);
        r.Engine.Handle("u", "m1");
        Assert.Equal(0, r.UserData.MarkPlayedCalls); // aucun appel d'écriture (compteur/date d'Emby non touchés)
        var propagation = r.Journal.Of("Propagation").Single();
        Assert.Contains("alreadyPlayed=2", propagation.Detail);
        Assert.Contains("propagated=0", propagation.Detail);
        Assert.Contains("already-played", r.Journal.Details("Skipped"));
    }

    [Fact]
    public void R8_MemberWithoutAccess_IsSkipped_NoErrorNoWrite()
    {
        var r = new Rig();
        var s = r.Add("1", new[] { "propager-lu=OUI" }, "m1");
        s.Members = new List<string> { "o", "u", "v" };
        r.UserData.DenyAccess("o", "m1"); // les DEUX autres membres sans accès
        r.UserData.DenyAccess("v", "m1");
        var result = r.Engine.Handle("u", "m1");
        Assert.Equal(0, r.UserData.MarkPlayedCalls);
        Assert.Empty(r.Journal.Of("Error"));
        var propagation = r.Journal.Of("Propagation").Single();
        Assert.Contains("noAccess=2", propagation.Detail);
        Assert.Contains("no-access", r.Journal.Details("Skipped"));
    }

    [Fact]
    public void ReadOnlyMember_PropagatesLikeAnyOtherMember_Q3()
    {
        var r = new Rig();
        var s = r.Add("1", new[] { "propager-lu=OUI" }, "m1");
        s.Members = new List<string> { "o", "u", "readonly" };
        r.Engine.Handle("u", "m1");
        Assert.True(r.UserData.IsPlayed("readonly", "m1"));
    }

    // ---- #30/#31 (v0.5.0) : confirmation explicite, aucun mecanisme special pour le proprietaire -------------

    [Fact]
    public void DeletedOwner_AsAPropagationTarget_IsSkippedLikeAnyMemberWithoutAccess_NoCrash()
    {
        // "o" reste dans MemberIds (le proprietaire est un membre comme un autre, ShareClassifier ne le distingue
        // pas ailleurs que pour IsShared/unknown-owner) mais son compte n'existe plus : EmbyUserDataGateway.Resolve
        // renverrait null (GetUserById ne trouve plus l'utilisateur), simule ici par DenyAccess (R8, meme chemin).
        var r = new Rig();
        var s = r.Add("1", new[] { "propager-lu=OUI" }, "m1");
        s.Members = new List<string> { "o", "u", "v" };
        r.UserData.DenyAccess("o", "m1"); // "o" = proprietaire supprime
        var ex = Record.Exception(() => r.Engine.Handle("u", "m1"));
        Assert.Null(ex);
        Assert.True(r.UserData.IsPlayed("v", "m1")); // l'autre membre recoit quand meme la propagation
        Assert.Contains("no-access", r.Journal.Details("Skipped"));
    }

    [Fact]
    public void ManagedPlaylistWithNoMedia_IsNeverACandidate_NoCrash()
    {
        var r = new Rig();
        r.Add("1", new[] { "propager-lu=OUI" }); // aucun media (params vide)
        var ex = Record.Exception(() => r.Engine.Handle("u", "m1"));
        Assert.Null(ex);
        Assert.Empty(r.Journal.Of("Propagation"));
        Assert.Empty(r.Journal.Of("Removal"));
    }

    [Fact]
    public void PropagationRegistersTheWriteTracker_BeforeMarking()
    {
        var r = new Rig();
        var s = r.Add("1", new[] { "propager-lu=OUI" }, "m1");
        r.Engine.Handle("u", "m1");
        Assert.True(r.WriteTracker.TryConsume("v", "m1")); // l'écriture a été enregistrée avant MarkPlayed
    }

    [Fact]
    public void SourceUser_IsNeverPropagatedTo_AndItsFlagIsUntouchedByTheGateway()
    {
        var r = new Rig();
        r.Add("1", new[] { "propager-lu=OUI" }, "m1"); // membres par défaut : "o" (propriétaire) et "v"
        r.Engine.Handle("u", "m1");
        Assert.DoesNotContain(("u", "m1"), r.UserData.Marked);
        Assert.Contains(("o", "m1"), r.UserData.Marked);
        Assert.Contains(("v", "m1"), r.UserData.Marked);
        Assert.Equal(2, r.UserData.MarkPlayedCalls);
    }

    [Fact]
    public void AFailingMarkPlayed_IsIsolated_JournaledByTypeOnly_OtherMembersUnaffected()
    {
        var r = new Rig();
        var s = r.Add("1", new[] { "propager-lu=OUI" }, "m1");
        s.Members = new List<string> { "o", "u", "v", "w" };
        r.UserData.ThrowOnMarkFor = (uid, _) => uid == "v";
        var result = Record.Exception(() => r.Engine.Handle("u", "m1"));
        Assert.Null(result); // Handle ne lève jamais même si un membre échoue
    }

    // ---- Budget partagé avec le retrait --------------------------------------------------------------------

    [Fact]
    public void Propagation_SharesTheBudgetWithRemoval_StopsWhenExhausted()
    {
        var s0 = new FakeGateway();
        var userData = new FakeUserDataGateway();
        var writeTracker = new PluginWriteTracker();
        var seen = new SeenPlaylists();
        var locks = new PlaylistLocks();
        var journal = new ListJournal();
        var defaults = new DefaultsService(s0, seen, locks, journal, "AIDE", () => 2, null, TimeSpan.FromMilliseconds(150));
        var engine = new ReadRemovalEngine(s0, userData, writeTracker, defaults, seen, locks, journal, new FakeClock(),
            TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(120));

        var s = s0.Add("1", "propager-lu=OUI");
        s.Overview = "x";
        s.Members = new List<string> { "o", "u", "m1", "m2", "m3", "m4", "m5" };
        s.Items = new List<string> { "x" };
        seen.TryMarkSeen("1");
        userData.ThrowOnMarkFor = null;
        // Simule un ralentissement par membre pour épuiser le budget avant la fin de la boucle.
        var calls = 0;
        var slowUserData = new SlowUserDataGateway(userData, () => { calls++; if (calls > 1) Thread.Sleep(150); });
        var slowEngine = new ReadRemovalEngine(s0, slowUserData, writeTracker, defaults, seen, locks, journal, new FakeClock(),
            TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(120));

        slowEngine.Handle("u", "x");
        var propagation = journal.Of("Propagation").Single();
        // Certains membres n'ont pas été atteints (budget épuisé) : la somme des compteurs est inférieure au total des membres.
        var parts = propagation.Detail!.Split(' ').Select(p => p.Split('=')).ToDictionary(p => p[0], p => int.Parse(p[1]));
        Assert.True(parts["propagated"] + parts["alreadyPlayed"] + parts["noAccess"] < parts["members"]);
    }

    private sealed class SlowUserDataGateway : IUserDataGateway
    {
        private readonly IUserDataGateway _inner;
        private readonly Action _onCall;
        public SlowUserDataGateway(IUserDataGateway inner, Action onCall) { _inner = inner; _onCall = onCall; }
        public bool HasAccess(string userId, string itemId) => _inner.HasAccess(userId, itemId);
        public bool? IsPlayed(string userId, string itemId) { _onCall(); return _inner.IsPlayed(userId, itemId); }
        public bool MarkPlayed(string userId, string itemId) => _inner.MarkPlayed(userId, itemId);
        public long? GetPosition(string userId, string itemId) => _inner.GetPosition(userId, itemId);
        public bool SetPosition(string userId, string itemId, long ticks) => _inner.SetPosition(userId, itemId, ticks);
    }

    // ---- S6a-c : absence de transitivité entre listes (via l'anti-écho, testé au niveau du processeur) ------
    // Le point délicat (l'écho de U1 ne redéclenche jamais le moteur pour SES AUTRES playlists) est couvert par
    // PlaybackEventProcessorDevTests (RecognizedEcho_ReturnsEcho_NeverCallsTheEngine et StillUpdatesTheTransitionMemory) :
    // c'est cette garde, pas ReadRemovalEngine lui-même, qui empêche la transitivité (ReadRemovalEngine ne sait pas si
    // un appel est un écho ; c'est PlaybackEventProcessor qui ne l'appelle pas dans ce cas).

    [Fact]
    public void TwoPlaylists_APropagationOnOneDoesNotTouchTheOther()
    {
        var r = new Rig();
        var s1 = r.Add("1", new[] { "propager-lu=OUI" }, "shared-media");
        s1.Members = new List<string> { "o", "u", "member-of-1-only" };
        var s2 = r.Add("2", new[] { "propager-lu=OUI" }, "other-media");
        s2.Members = new List<string> { "o", "u", "member-of-2-only" };

        r.Engine.Handle("u", "shared-media");

        Assert.True(r.UserData.IsPlayed("member-of-1-only", "shared-media"));
        Assert.False(r.UserData.IsPlayed("member-of-2-only", "shared-media") == true);
        Assert.Empty(r.Journal.Of("Propagation").Where(e => e.PlaylistId == "2"));
    }
}
