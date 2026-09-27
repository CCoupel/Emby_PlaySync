using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Marker;
using EmbySharedPlaylist.Reconciliation;
using Xunit;

namespace EmbySharedPlaylist.Tests;

internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
}

internal sealed class ListJournal : IJournal
{
    private readonly List<JournalEntry> _entries = new();
    public IReadOnlyList<JournalEntry> Entries { get { lock (_entries) return _entries.ToList(); } }
    public void Add(JournalEntry entry) { lock (_entries) _entries.Add(entry); }
    public IEnumerable<JournalEntry> Of(string kind) => Entries.Where(e => e.Kind == kind);
    public IEnumerable<string?> Details(string kind) => Of(kind).Select(e => e.Detail);
}

/// <summary>Passerelle simulée du flag lu (#20) et de la position (#45) : accès/état par couple (utilisateur, média), sans SDK.</summary>
internal sealed class FakeUserDataGateway : IUserDataGateway
{
    private readonly Dictionary<(string UserId, string ItemId), bool> _played = new();
    private readonly Dictionary<(string UserId, string ItemId), long> _positions = new();
    private readonly HashSet<(string UserId, string ItemId)> _noAccess = new();
    public int MarkPlayedCalls;
    public int SetPositionCalls;
    public readonly List<(string UserId, string ItemId)> Marked = new();
    public readonly List<(string UserId, string ItemId, long Ticks)> PositionsSet = new();
    public Func<string, string, bool>? ThrowOnMarkFor;
    public Func<string, string, bool>? ThrowOnSetPositionFor;

    /// <summary>Par défaut : accès, non lu, position 0. <see cref="DenyAccess"/>/<see cref="SetPlayed"/>/<see cref="SetPosition"/> changent l'état avant l'appel testé.</summary>
    public void DenyAccess(string userId, string itemId) => _noAccess.Add((userId, itemId));

    public void SetPlayed(string userId, string itemId, bool played) => _played[(userId, itemId)] = played;

    public bool HasAccess(string userId, string itemId) => !_noAccess.Contains((userId, itemId));

    public bool? IsPlayed(string userId, string itemId)
    {
        if (_noAccess.Contains((userId, itemId))) return null;
        return _played.TryGetValue((userId, itemId), out var played) && played;
    }

    public bool MarkPlayed(string userId, string itemId)
    {
        Interlocked.Increment(ref MarkPlayedCalls);
        if (ThrowOnMarkFor?.Invoke(userId, itemId) == true) throw new InvalidOperationException("écriture en échec avec un message secret");
        lock (Marked) Marked.Add((userId, itemId));
        _played[(userId, itemId)] = true;
        return true;
    }

    public long? GetPosition(string userId, string itemId)
    {
        if (_noAccess.Contains((userId, itemId))) return null;
        return _positions.TryGetValue((userId, itemId), out var t) ? t : 0L;
    }

    public bool SetPosition(string userId, string itemId, long ticks)
    {
        Interlocked.Increment(ref SetPositionCalls);
        if (_noAccess.Contains((userId, itemId))) return false; // R8
        if (ThrowOnSetPositionFor?.Invoke(userId, itemId) == true) throw new InvalidOperationException("écriture en échec avec un message secret");
        var current = _positions.TryGetValue((userId, itemId), out var t) ? t : 0L;
        if (current == ticks) return false; // déjà cette position
        _positions[(userId, itemId)] = ticks;
        lock (PositionsSet) PositionsSet.Add((userId, itemId, ticks));
        return true;
    }
}

/// <summary>Passerelle simulée de la permission de partage (#26) : par utilisateur connu, sharing on/off ; jamais de révocation.</summary>
internal sealed class FakeUserPolicyGateway : IUserPolicyGateway
{
    private readonly Dictionary<string, bool> _sharing = new();
    public readonly List<string> Enabled = new();
    public int AllUserIdsCalls;
    public bool ThrowOnAllUserIds;
    public Func<string, bool>? ThrowOnEnableFor;

    public void AddUser(string userId, bool sharingEnabled = false) => _sharing[userId] = sharingEnabled;

    /// <summary>Simule un décochage manuel par l'admin (D-e) : remet à faux sans passer par EnableSharingIfNeeded.</summary>
    public void ManuallyDisable(string userId) => _sharing[userId] = false;

    public IReadOnlyList<string> AllUserIds()
    {
        Interlocked.Increment(ref AllUserIdsCalls);
        if (ThrowOnAllUserIds) throw new InvalidOperationException("liste en échec avec un message secret");
        return _sharing.Keys.ToList();
    }

    public bool? IsSharingEnabled(string userId) => _sharing.TryGetValue(userId, out var v) ? v : (bool?)null;

    public bool EnableSharingIfNeeded(string userId)
    {
        if (!_sharing.ContainsKey(userId)) return false; // utilisateur inconnu
        if (ThrowOnEnableFor?.Invoke(userId) == true) throw new InvalidOperationException("écriture en échec avec un message secret");
        if (_sharing[userId]) return false; // déjà actif : jamais réécrit, jamais révoqué
        _sharing[userId] = true;
        lock (Enabled) Enabled.Add(userId);
        return true;
    }
}

/// <summary>Passerelle simulée : mêmes garanties que la vraie (ajout seul, re-vérification à l'écriture).</summary>
internal sealed class FakeGateway : IPlaylistGateway
{
    public sealed class State
    {
        public string? Owner = "o";
        public List<string> Members = new() { "o", "m" };
        public List<string> Tags = new();
        public string? Overview;
        /// <summary>Médias de la playlist (un id par entrée : les doublons sont possibles).</summary>
        public List<string> Items = new();
    }

    public readonly Dictionary<string, State> Playlists = new();
    public int ApplyCalls;
    public int ListCalls;
    public int RemoveCalls;
    public Func<string, bool>? ThrowOnRemoveFor;
    public Action<string>? OnRemove;
    public int GetCalls;
    public Func<string, bool>? ThrowOnApplyFor;
    public bool ThrowOnList;
    public Action<string>? OnApply;
    public readonly object Gate = new();

    public State Add(string id, params string[] tags)
    {
        var s = new State { Tags = tags.ToList() };
        Playlists[id] = s;
        return s;
    }

    private PlaylistSnapshot Snap(string id, State s) => new(id, s.Owner, s.Members.ToList(), s.Tags.ToList(), s.Overview);

    public IReadOnlyList<PlaylistSnapshot> ListSharedPlaylists()
    {
        Interlocked.Increment(ref ListCalls);
        if (ThrowOnList) throw new InvalidOperationException("liste");
        lock (Gate) return Playlists.Select(p => Snap(p.Key, p.Value)).Where(s => s.IsShared).ToList();
    }

    public IReadOnlyList<PlaylistSnapshot> ListSharedPlaylistsOfUserContaining(string userId, string itemId)
    {
        lock (Gate)
            return Playlists.Select(p => Snap(p.Key, p.Value))
                .Where(s => s.IsShared && s.MemberIds.Contains(userId) && Playlists[s.Id].Items.Contains(itemId))
                .ToList();
    }

    public PlaylistSnapshot? Get(string playlistId)
    {
        Interlocked.Increment(ref GetCalls);
        lock (Gate) return Playlists.TryGetValue(playlistId, out var s) ? Snap(playlistId, s) : null;
    }

    public bool RemoveOneEntry(string playlistId, string itemId)
    {
        Interlocked.Increment(ref RemoveCalls);
        OnRemove?.Invoke(playlistId);
        if (ThrowOnRemoveFor?.Invoke(playlistId) == true) throw new InvalidOperationException("retrait en échec avec un message secret");
        lock (Gate) return Playlists[playlistId].Items.Remove(itemId);
    }

    public ApplyResult ApplyDefaults(string playlistId, IReadOnlyList<MarkerFamily> familiesToPose, OverviewChange? overview)
    {
        Interlocked.Increment(ref ApplyCalls);
        OnApply?.Invoke(playlistId);
        if (ThrowOnApplyFor?.Invoke(playlistId) == true) throw new InvalidOperationException("écriture en échec avec un message secret");
        lock (Gate)
        {
            var s = Playlists[playlistId];
            var posed = new List<MarkerFamily>();
            foreach (var f in familiesToPose)
            {
                if (MarkerEvaluator.Evaluate(s.Tags, f) != MarkerState.None) continue; // re-vérification à l'écriture
                s.Tags.Add(MarkerEvaluator.NonTag(f));
                posed.Add(f);
            }
            var wrote = false;
            // RequiredCurrent null = n'écrit que si vide ; sinon = n'écrit que si encore égale exactement (re-vérifié ici).
            if (overview != null)
            {
                var matches = overview.RequiredCurrent == null
                    ? string.IsNullOrWhiteSpace(s.Overview)
                    : string.Equals(s.Overview, overview.RequiredCurrent, StringComparison.Ordinal);
                if (matches) { s.Overview = overview.NewValue; wrote = true; }
            }
            return new ApplyResult(posed, wrote);
        }
    }
}

public class DefaultsServiceDevTests
{
    private const string Help = "AIDE";

    private sealed class Rig
    {
        public readonly FakeGateway Gateway = new();
        public readonly SeenPlaylists Seen = new();
        public readonly PlaylistLocks Locks = new();
        public readonly ListJournal Journal = new();
        public int Grace = 2;
        public DefaultsService Service;

        public Rig()
        {
            Service = new DefaultsService(Gateway, Seen, Locks, Journal, Help, () => Grace, new FakeClock(), TimeSpan.FromMilliseconds(150));
        }

        public PlaylistSnapshot Snapshot(string id) => Gateway.Get(id)!;
    }

    [Fact]
    public void FirstDetection_PosesBothNonAndTheHelpMessage_KeepingOwnerTags()
    {
        var r = new Rig();
        var s = r.Gateway.Add("1", "famille", "noel");
        var outcome = r.Service.OnFirstDetection(r.Snapshot("1"));

        Assert.Equal(new[] { "famille", "noel", "remove-si-lu=NON", "propager-lu=NON" }, s.Tags);
        Assert.Equal(Help, s.Overview);
        Assert.Equal(2, outcome.MarkersPosed);
        Assert.True(outcome.DescriptionWritten);
        Assert.Equal(new[] { "family=remove-si-lu cause=first-detection", "family=propager-lu cause=first-detection" }, r.Journal.Details("MarkerPosed"));
        Assert.Equal(new[] { "cause=first-detection" }, r.Journal.Details("DescriptionWritten"));
        Assert.True(r.Seen.IsSeen("1"));
    }

    [Fact]
    public void FirstDetection_OnlyPosesTheMissingFamily()
    {
        var r = new Rig();
        var s = r.Gateway.Add("1", "propager-lu=OUI");
        r.Service.OnFirstDetection(r.Snapshot("1"));
        Assert.Equal(new[] { "propager-lu=OUI", "remove-si-lu=NON" }, s.Tags);
    }

    [Fact]
    public void FirstDetection_NeverTouchesAnExistingOuiOrBoth()
    {
        var r = new Rig();
        var s = r.Gateway.Add("1", "remove-si-lu=OUI", "remove-si-lu=NON", "propager-lu=oui");
        s.Overview = "mon texte";
        var outcome = r.Service.OnFirstDetection(r.Snapshot("1"));
        Assert.Equal(new[] { "remove-si-lu=OUI", "remove-si-lu=NON", "propager-lu=oui" }, s.Tags);
        Assert.Equal("mon texte", s.Overview);
        Assert.True(outcome.Skipped);
        Assert.Equal(0, r.Gateway.ApplyCalls);
        Assert.Equal(new[] { "marker-present" }, r.Journal.Details("Skipped"));
        Assert.True(r.Seen.IsSeen("1"));
    }

    [Fact]
    public void Description_IsWrittenOnlyWhenEmpty()
    {
        var r = new Rig();
        var s = r.Gateway.Add("1");
        s.Overview = "texte du propriétaire";
        r.Service.OnFirstDetection(r.Snapshot("1"));
        Assert.Equal("texte du propriétaire", s.Overview);
        Assert.Empty(r.Journal.Of("DescriptionWritten"));
    }

    [Fact]
    public void SecondFirstDetection_IsSkipped_NoSecondWrite()
    {
        var r = new Rig();
        r.Gateway.Add("1");
        r.Service.OnFirstDetection(r.Snapshot("1"));
        var outcome = r.Service.OnFirstDetection(r.Snapshot("1"));
        Assert.True(outcome.Skipped);
        Assert.Equal(1, r.Gateway.ApplyCalls);
        Assert.Contains("already-seen", r.Journal.Details("Skipped"));
    }

    [Fact]
    public void Playlist_IsMarkedSeenBeforeTheWrite_AndTheWriteRunsInAWriteScope_UnderTheLock()
    {
        var r = new Rig();
        r.Gateway.Add("1");
        bool? seenDuringWrite = null, scopeDuringWrite = null, lockDuringWrite = null;
        r.Gateway.OnApply = id =>
        {
            seenDuringWrite = r.Seen.IsSeen(id);
            scopeDuringWrite = WriteScope.Active;
            lockDuringWrite = r.Locks.IsHeldByCurrentThread(id);
        };
        r.Service.OnFirstDetection(r.Snapshot("1"));
        Assert.True(seenDuringWrite);
        Assert.True(scopeDuringWrite);
        Assert.True(lockDuringWrite);
        Assert.False(WriteScope.Active);
        Assert.False(r.Locks.IsHeldByCurrentThread("1"));
    }

    [Fact]
    public void WriteFailure_IsIsolated_JournaledByTypeOnly_AndThePlaylistStaysSeen()
    {
        var r = new Rig();
        r.Gateway.Add("1");
        r.Gateway.ThrowOnApplyFor = _ => true;
        var outcome = r.Service.OnFirstDetection(r.Snapshot("1"));
        Assert.False(outcome.DescriptionWritten);
        Assert.Equal(new[] { "InvalidOperationException" }, r.Journal.Details("Error"));
        Assert.DoesNotContain("secret", string.Join(" ", r.Journal.Entries.Select(e => e.Detail)));
        Assert.True(r.Seen.IsSeen("1"));
    }

    [Fact]
    public void LockBusy_IsSkippedWithoutWritingOrMarkingSeen()
    {
        var r = new Rig();
        r.Gateway.Add("1");
        var snap = r.Snapshot("1");
        var held = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var t = new Thread(() => { using var l = r.Locks.TryAcquire("1", TimeSpan.FromSeconds(5)); held.Set(); release.Wait(); });
        t.Start(); held.Wait();
        try
        {
            var outcome = r.Service.OnFirstDetection(snap);
            Assert.True(outcome.Skipped);
            Assert.Equal(0, r.Gateway.ApplyCalls);
            Assert.False(r.Seen.IsSeen("1"));
            Assert.Contains("lock-busy", r.Journal.Details("Skipped"));
        }
        finally { release.Set(); t.Join(); }
    }

    [Fact]
    public void ConcurrentFirstDetections_WriteOnce()
    {
        var r = new Rig();
        r.Gateway.Add("1");
        var snap = r.Snapshot("1");
        var svc = new DefaultsService(r.Gateway, r.Seen, r.Locks, r.Journal, Help, () => 2, null, TimeSpan.FromSeconds(5));
        Parallel.For(0, 16, _ => svc.OnFirstDetection(snap));
        Assert.Equal(1, r.Gateway.ApplyCalls);
        Assert.Equal(2, r.Journal.Of("MarkerPosed").Count());
    }

    // ---- OnPass : grâce -------------------------------------------------------------------------

    [Fact]
    public void Grace_AbsentFamilyIsReposedAfterGracePasses_ThenCounterResets()
    {
        var r = new Rig();
        var s = r.Gateway.Add("1", "propager-lu=NON");
        s.Overview = "x";
        r.Seen.TryMarkSeen("1");

        var first = r.Service.OnPass(r.Snapshot("1"));
        Assert.Equal(0, first.MarkersPosed);
        Assert.Equal(1, first.PendingGrace);
        Assert.Equal(0, r.Gateway.ApplyCalls);

        var second = r.Service.OnPass(r.Snapshot("1"));
        Assert.Equal(1, second.MarkersPosed);
        Assert.Contains("remove-si-lu=NON", s.Tags);
        Assert.Equal(new[] { "family=remove-si-lu cause=grace-elapsed" }, r.Journal.Details("MarkerPosed"));
        Assert.Equal(0, r.Seen.Get("1", "remove-si-lu"));
    }

    [Fact]
    public void Grace_RemoveNonThenAddOui_IsNeverReposed()
    {
        var r = new Rig();
        var s = r.Gateway.Add("1");
        s.Overview = "x";
        s.Tags.Add("propager-lu=NON");
        r.Seen.TryMarkSeen("1");

        r.Service.OnPass(r.Snapshot("1"));            // état intermédiaire : remove-si-lu absent (compteur 1)
        s.Tags.Add("remove-si-lu=OUI");               // le propriétaire a terminé son édition
        r.Service.OnPass(r.Snapshot("1"));            // famille présente : compteur remis à zéro
        r.Service.OnPass(r.Snapshot("1"));
        r.Service.OnPass(r.Snapshot("1"));

        Assert.Equal(0, r.Gateway.ApplyCalls);
        Assert.Equal(new[] { "propager-lu=NON", "remove-si-lu=OUI" }, s.Tags);
        Assert.Equal(0, r.Seen.Get("1", "remove-si-lu"));
    }

    [Fact]
    public void Grace_AddOuiThenRemoveNon_IsNeverReposed()
    {
        var r = new Rig();
        var s = r.Gateway.Add("1", "remove-si-lu=NON", "propager-lu=NON");
        s.Overview = "x";
        r.Seen.TryMarkSeen("1");
        s.Tags.Add("remove-si-lu=OUI");   // {NON, OUI}
        r.Service.OnPass(r.Snapshot("1"));
        s.Tags.Remove("remove-si-lu=NON"); // {OUI}
        for (var i = 0; i < 5; i++) r.Service.OnPass(r.Snapshot("1"));
        Assert.Equal(0, r.Gateway.ApplyCalls);
    }

    [Fact]
    public void Grace_IsPerFamily()
    {
        var r = new Rig();
        var s = r.Gateway.Add("1");
        s.Overview = "x";
        r.Seen.TryMarkSeen("1");
        r.Service.OnPass(r.Snapshot("1"));
        s.Tags.Add("propager-lu=NON");                 // seule propager-lu est revenue
        var second = r.Service.OnPass(r.Snapshot("1"));
        Assert.Equal(new[] { "propager-lu=NON", "remove-si-lu=NON" }, s.Tags);
        Assert.Equal(1, second.MarkersPosed);
    }

    [Fact]
    public void Grace_EmptiedDescription_IsRewrittenAfterGracePasses_NeverOverText()
    {
        var r = new Rig();
        var s = r.Gateway.Add("1", "remove-si-lu=NON", "propager-lu=NON");
        r.Seen.TryMarkSeen("1");
        r.Service.OnPass(r.Snapshot("1"));
        Assert.Null(s.Overview);
        var second = r.Service.OnPass(r.Snapshot("1"));
        Assert.True(second.DescriptionWritten);
        Assert.Equal(Help, s.Overview);
        Assert.Equal(new[] { "cause=grace-elapsed" }, r.Journal.Details("DescriptionWritten"));

        s.Overview = "mon propre texte";
        r.Service.OnPass(r.Snapshot("1"));
        r.Service.OnPass(r.Snapshot("1"));
        Assert.Equal("mon propre texte", s.Overview);
    }

    [Fact]
    public void Grace_TextAppearingResetsTheDescriptionCounter()
    {
        var r = new Rig();
        var s = r.Gateway.Add("1", "remove-si-lu=NON", "propager-lu=NON");
        r.Seen.TryMarkSeen("1");
        r.Service.OnPass(r.Snapshot("1"));
        s.Overview = "texte";
        r.Service.OnPass(r.Snapshot("1"));
        Assert.Equal(0, r.Seen.Get("1", "description"));
        s.Overview = null;
        r.Service.OnPass(r.Snapshot("1"));              // recommence à 1 : pas encore réécrit
        Assert.Null(s.Overview);
    }

    [Fact]
    public void Grace_UsesTheCurrentConfigurationEachPass_WithAMinimumOfOne()
    {
        var r = new Rig();
        var s = r.Gateway.Add("1", "propager-lu=NON");
        s.Overview = "x";
        r.Seen.TryMarkSeen("1");
        r.Grace = 0; // valeur invalide : traitée comme 1
        var outcome = r.Service.OnPass(r.Snapshot("1"));
        Assert.Equal(1, outcome.MarkersPosed);
    }

    [Fact]
    public void TwoPasses_NeverDuplicateTags()
    {
        var r = new Rig();
        var s = r.Gateway.Add("1");
        r.Service.OnFirstDetection(r.Snapshot("1"));
        for (var i = 0; i < 4; i++) r.Service.OnPass(r.Snapshot("1"));
        Assert.Equal(new[] { "remove-si-lu=NON", "propager-lu=NON" }, s.Tags);
        Assert.Equal(1, r.Gateway.ApplyCalls);
    }

    [Fact]
    public void OnPass_OnAnUnseenPlaylist_RunsTheFirstDetection()
    {
        var r = new Rig();
        var s = r.Gateway.Add("1");
        var outcome = r.Service.OnPass(r.Snapshot("1"));
        Assert.Equal(2, outcome.MarkersPosed);
        Assert.Contains("family=remove-si-lu cause=first-detection", r.Journal.Details("MarkerPosed"));
        Assert.True(r.Seen.IsSeen("1"));
    }

    [Fact]
    public void GateWrite_ReverifiesAtWriteTime_OwnerAddedOuiBetweenReadAndWrite()
    {
        var r = new Rig();
        var s = r.Gateway.Add("1");
        var stale = r.Snapshot("1");               // lu sans étiquette
        s.Tags.Add("remove-si-lu=OUI");            // le propriétaire agit entre la lecture et l'écriture
        var outcome = r.Service.OnFirstDetection(stale);
        Assert.Equal(new[] { "remove-si-lu=OUI", "propager-lu=NON" }, s.Tags);
        Assert.Equal(1, outcome.MarkersPosed);
    }
}

public class ReconciliationServiceDevTests
{
    private sealed class Rig
    {
        public readonly FakeGateway Gateway = new();
        public readonly SeenPlaylists Seen = new();
        public readonly PlaylistLocks Locks = new();
        public readonly ListJournal Journal = new();
        public readonly FakeClock Clock = new();
        public readonly ReconciliationService Service;

        public Rig()
        {
            var defaults = new DefaultsService(Gateway, Seen, Locks, Journal, "AIDE", () => 2, Clock, TimeSpan.FromMilliseconds(150));
            Service = new ReconciliationService(Gateway, defaults, Seen, Locks, Journal, Clock, TimeSpan.FromMilliseconds(150));
        }
    }

    [Fact]
    public void FirstPass_PosesEverywhere_AndJournalsScanPass()
    {
        var r = new Rig();
        r.Gateway.Add("1"); r.Gateway.Add("2", "remove-si-lu=OUI");
        var result = r.Service.RunPass();

        Assert.Equal(2, result.Shared);
        Assert.Equal(3, result.Posed);       // 2 + 1
        Assert.Equal(0, result.Pending);
        var detail = r.Journal.Details("ScanPass").Single();
        Assert.StartsWith("playlists=2 shared=2 posed=3 pending=0 durationMs=", detail);
        Assert.True(r.Seen.IsSeen("1") && r.Seen.IsSeen("2"));
    }

    [Fact]
    public void SecondPass_IsIdempotent_NoWrite()
    {
        var r = new Rig();
        r.Gateway.Add("1");
        r.Service.RunPass();
        var calls = r.Gateway.ApplyCalls;
        var result = r.Service.RunPass();
        Assert.Equal(calls, r.Gateway.ApplyCalls);
        Assert.Equal(0, result.Posed);
        Assert.Equal(0, result.Pending);
    }

    [Fact]
    public void NotSharedPlaylists_AreIgnored()
    {
        var r = new Rig();
        var s = r.Gateway.Add("1");
        s.Members = new List<string> { "o" };
        var result = r.Service.RunPass();
        Assert.Equal(0, result.Shared);
        Assert.Equal(0, r.Gateway.ApplyCalls);
    }

    [Fact]
    public void AFailingPlaylist_DoesNotStopTheOthers()
    {
        var r = new Rig();
        r.Gateway.Add("1"); r.Gateway.Add("2");
        r.Gateway.ThrowOnApplyFor = id => id == "1";
        var result = r.Service.RunPass();
        Assert.Equal(2, result.Shared);
        Assert.Equal(new[] { "InvalidOperationException" }, r.Journal.Details("Error"));
        Assert.Contains("2", r.Journal.Of("MarkerPosed").Select(e => e.PlaylistId));
    }

    [Fact]
    public void AFailingListing_IsJournaled_AndThePassStillEnds()
    {
        var r = new Rig();
        r.Gateway.ThrowOnList = true;
        var result = r.Service.RunPass();
        Assert.Equal(0, result.Shared);
        Assert.Single(r.Journal.Of("Error"));
        Assert.Single(r.Journal.Of("ScanPass"));
    }

    [Fact]
    public void BusyPlaylist_IsCountedPending_AndSkippedWithoutBlocking()
    {
        var r = new Rig();
        r.Gateway.Add("1");
        var held = new ManualResetEventSlim(); var release = new ManualResetEventSlim();
        var t = new Thread(() => { using var l = r.Locks.TryAcquire("1", TimeSpan.FromSeconds(5)); held.Set(); release.Wait(); });
        t.Start(); held.Wait();
        try
        {
            var result = r.Service.RunPass();
            Assert.Equal(1, result.Pending);
            Assert.Equal(0, r.Gateway.ApplyCalls);
            Assert.Contains("lock-busy", r.Journal.Details("Skipped"));
        }
        finally { release.Set(); t.Join(); }
    }

    [Fact]
    public void GracePasses_AreCountedAcrossPasses()
    {
        var r = new Rig();
        var s = r.Gateway.Add("1");
        s.Overview = "x";
        r.Service.RunPass();                               // première détection : deux NON
        s.Tags.Remove("propager-lu=NON");                  // le propriétaire supprime la famille
        var p2 = r.Service.RunPass();
        Assert.Equal(1, p2.Pending);
        Assert.DoesNotContain("propager-lu=NON", s.Tags);
        r.Service.RunPass();
        Assert.Contains("propager-lu=NON", s.Tags);
    }

    [Fact]
    public void LastPass_IsRecorded()
    {
        var r = new Rig();
        r.Gateway.Add("1");
        Assert.Null(r.Service.LastPass);
        r.Service.RunPass();
        Assert.Equal(r.Clock.UtcNow, r.Service.LastPass!.Ts);
        Assert.Equal(1, r.Service.LastPass.SharedManaged);
    }

    [Fact]
    public void Cancellation_StopsThePass()
    {
        var r = new Rig();
        r.Gateway.Add("1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => r.Service.RunPass(cts.Token));
    }

    [Fact]
    public void PassConcurrentWithFirstDetections_WritesEachPlaylistOnce()
    {
        var r = new Rig();
        for (var i = 1; i <= 8; i++) r.Gateway.Add(i.ToString());
        var defaults = new DefaultsService(r.Gateway, r.Seen, r.Locks, r.Journal, "AIDE", () => 2, r.Clock, TimeSpan.FromSeconds(5));
        var svc = new ReconciliationService(r.Gateway, defaults, r.Seen, r.Locks, r.Journal, r.Clock, TimeSpan.FromSeconds(5));
        Parallel.Invoke(
            () => svc.RunPass(),
            () => { for (var i = 1; i <= 8; i++) defaults.OnFirstDetection(r.Gateway.Get(i.ToString())!); },
            () => svc.RunPass());
        Assert.Equal(8, r.Gateway.ApplyCalls);
        Assert.All(r.Gateway.Playlists.Values, s => Assert.Equal(new[] { "remove-si-lu=NON", "propager-lu=NON" }, s.Tags));
    }
}

public class FirstDetectionCoordinatorDevTests
{
    private sealed class Rig
    {
        public readonly FakeGateway Gateway = new();
        public readonly SeenPlaylists Seen = new();
        public readonly ListJournal Journal = new();
        public readonly FirstDetectionCoordinator Coordinator;

        public Rig()
        {
            var defaults = new DefaultsService(Gateway, Seen, new PlaylistLocks(), Journal, "AIDE", () => 2, null, TimeSpan.FromMilliseconds(150));
            Coordinator = new FirstDetectionCoordinator(Gateway, defaults, Seen, Journal);
        }
    }

    [Fact]
    public void UnseenSharedPlaylist_GetsTheFirstDetection()
    {
        var r = new Rig();
        var s = r.Gateway.Add("1");
        r.Coordinator.OnPlaylistEvent("1");
        Assert.Equal(new[] { "remove-si-lu=NON", "propager-lu=NON" }, s.Tags);
        Assert.True(r.Seen.IsSeen("1"));
    }

    [Fact]
    public void AlreadySeenPlaylist_IsNeverTouched_NoRead()
    {
        var r = new Rig();
        r.Gateway.Add("1");
        r.Seen.TryMarkSeen("1");
        r.Coordinator.OnPlaylistEvent("1");
        Assert.Equal(0, r.Gateway.GetCalls);
        Assert.Equal(0, r.Gateway.ApplyCalls);
        Assert.Equal(new[] { "already-seen" }, r.Journal.Details("Skipped"));
    }

    [Fact]
    public void EchoOfOurOwnWrite_IsIgnoredInAWriteScope()
    {
        var r = new Rig();
        r.Gateway.Add("1");
        using (WriteScope.Enter()) r.Coordinator.OnPlaylistEvent("1");
        Assert.Equal(new[] { "reentrant" }, r.Journal.Details("Skipped"));
        Assert.Equal(0, r.Gateway.ApplyCalls);
        Assert.False(r.Seen.IsSeen("1"));
    }

    [Fact]
    public void NotSharedPlaylist_IsSkipped_AndNotMarkedSeen()
    {
        var r = new Rig();
        r.Gateway.Add("1").Members = new List<string> { "o" };
        r.Coordinator.OnPlaylistEvent("1");
        Assert.Equal(new[] { "not-shared" }, r.Journal.Details("Skipped"));
        Assert.False(r.Seen.IsSeen("1"));
        Assert.Equal(0, r.Gateway.ApplyCalls);
    }

    [Fact]
    public void UnknownOwner_IsSkipped()
    {
        var r = new Rig();
        r.Gateway.Add("1").Owner = null;
        r.Coordinator.OnPlaylistEvent("1");
        Assert.Equal(new[] { "unknown-owner" }, r.Journal.Details("Skipped"));
    }

    [Fact]
    public void UnknownPlaylist_IsIgnored()
    {
        var r = new Rig();
        r.Coordinator.OnPlaylistEvent("999");
        Assert.Empty(r.Journal.Entries);
    }

    [Fact]
    public void ANotSharedPlaylistThatBecomesSharedIsDetectedOnTheNextEvent()
    {
        var r = new Rig();
        var s = r.Gateway.Add("1");
        s.Members = new List<string> { "o" };
        r.Coordinator.OnPlaylistEvent("1");
        s.Members = new List<string> { "o", "m" };
        r.Coordinator.OnPlaylistEvent("1");
        Assert.Equal(2, s.Tags.Count);
    }

    [Fact]
    public void ExceptionsNeverEscape_AndAreJournaledByTypeOnly()
    {
        var r = new Rig();
        var throwing = new ThrowingGateway();
        var defaults = new DefaultsService(throwing, r.Seen, new PlaylistLocks(), r.Journal, "AIDE", () => 2);
        var coordinator = new FirstDetectionCoordinator(throwing, defaults, r.Seen, r.Journal);
        Assert.Null(Record.Exception(() => coordinator.OnPlaylistEvent("1")));
        Assert.Equal(new[] { "InvalidOperationException" }, r.Journal.Details("Error"));
    }

    private sealed class ThrowingGateway : IPlaylistGateway
    {
        public IReadOnlyList<PlaylistSnapshot> ListSharedPlaylists() => throw new InvalidOperationException("secret");
        public IReadOnlyList<PlaylistSnapshot> ListSharedPlaylistsOfUserContaining(string userId, string itemId) => throw new InvalidOperationException("secret");
        public PlaylistSnapshot? Get(string playlistId) => throw new InvalidOperationException("secret");
        public bool RemoveOneEntry(string playlistId, string itemId) => throw new InvalidOperationException("secret");
        public ApplyResult ApplyDefaults(string playlistId, IReadOnlyList<MarkerFamily> familiesToPose, OverviewChange? overview) => throw new InvalidOperationException("secret");
    }
}

public class HelpTextDevTests
{
    [Fact]
    public void Message_MentionsBothTagsAndTheRule()
    {
        Assert.Contains("remove-si-lu=OUI", HelpText.Message);
        Assert.Contains("propager-lu=OUI", HelpText.Message);
        Assert.Contains("NON l'emporte", HelpText.Message);
        Assert.StartsWith("Playlist partagée gérée par Emby Shared Playlist.", HelpText.Message);
        Assert.InRange(HelpText.Message.Length, 300, 700);
    }

    [Fact]
    public void Message_HasNoPersonalOrSecretData()
    {
        foreach (var forbidden in new[] { "http", "token", "@", "\\", "/config" })
            Assert.DoesNotContain(forbidden, HelpText.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public class HelpTextV2DevTests
{
    [Fact]
    public void V2_MentionsPropagationEffective_NoLongerAFutureFeature()
    {
        Assert.Contains("propager-lu=OUI", HelpText.V2);
        Assert.DoesNotContain("fonction à venir", HelpText.V2);
        Assert.Contains("posé chez les autres", HelpText.V2);
    }

    [Fact]
    public void V1AndV2_ShareEverythingExceptThePropagerLuLine()
    {
        var v1Lines = HelpText.V1.Split('\n');
        var v2Lines = HelpText.V2.Split('\n');
        Assert.Equal(v1Lines.Length, v2Lines.Length);
        var diff = v1Lines.Zip(v2Lines, (a, b) => a == b).Count(same => !same);
        Assert.Equal(1, diff); // seule la ligne propager-lu change
    }

    [Fact]
    public void Message_IsAnAliasOfV1()
    {
        Assert.Equal(HelpText.V1, HelpText.Message);
    }
}

/// <summary>#51 : remplacement conditionnel du message d'aide v0.2.0 -> v0.3.0, sans aucun état mémorisé.</summary>
public class DefaultsServiceHelpTextReplacementDevTests
{
    private sealed class Rig
    {
        public readonly FakeGateway Gateway = new();
        public readonly SeenPlaylists Seen = new();
        public readonly PlaylistLocks Locks = new();
        public readonly ListJournal Journal = new();
        public int Grace = 2;
        public readonly DefaultsService Service;

        public Rig() => Service = new DefaultsService(Gateway, Seen, Locks, Journal, HelpText.V1, () => Grace, new FakeClock(), TimeSpan.FromMilliseconds(150));

        public PlaylistSnapshot Snapshot(string id) => Gateway.Get(id)!;
    }

    [Fact]
    public void FirstDetection_MarkerPosedAndDescriptionV1ToV2_HaveDistinctCauses()
    {
        // Cas croisé (revue, I10.cause) : la playlist reçoit sa PREMIÈRE pose de marqueur ET sa description vaut déjà
        // exactement V1, au même passage. Les deux causes doivent rester distinctes dans le journal.
        var r = new Rig();
        var s = r.Gateway.Add("1", "remove-si-lu=OUI"); // propager-lu absent : première pose de ce seul marqueur
        s.Overview = HelpText.V1;
        r.Service.OnFirstDetection(r.Snapshot("1"));

        Assert.Equal(new[] { "family=propager-lu cause=first-detection" }, r.Journal.Details("MarkerPosed"));
        Assert.Equal(new[] { "cause=v1-to-v2" }, r.Journal.Details("DescriptionWritten"));
        Assert.Equal(HelpText.V2, s.Overview);
        Assert.Contains("propager-lu=NON", s.Tags);
    }

    [Fact]
    public void OnPass_MarkerPosedAndDescriptionV1ToV2_HaveDistinctCauses()
    {
        var r = new Rig();
        var s = r.Gateway.Add("1"); // les deux marqueurs absents
        s.Overview = HelpText.V1;
        r.Seen.TryMarkSeen("1");
        r.Service.OnPass(r.Snapshot("1")); // 1re passe : compteurs de grâce démarrés, rien posé encore
        var outcome = r.Service.OnPass(r.Snapshot("1")); // 2e passe : grâce atteinte pour les marqueurs

        Assert.Equal(2, outcome.MarkersPosed);
        Assert.All(r.Journal.Details("MarkerPosed"), d => Assert.EndsWith("cause=grace-elapsed", d));
        Assert.Equal(new[] { "cause=v1-to-v2" }, r.Journal.Details("DescriptionWritten")); // jamais "grace-elapsed" ici
        Assert.Equal(HelpText.V2, s.Overview);
    }

    [Fact]
    public void FirstDetection_ExactV1_IsReplacedByV2_Immediately()
    {
        var r = new Rig();
        var s = r.Gateway.Add("1", "remove-si-lu=OUI", "propager-lu=OUI"); // familles déjà présentes : seule la description change
        s.Overview = HelpText.V1;
        var outcome = r.Service.OnFirstDetection(r.Snapshot("1"));
        Assert.Equal(HelpText.V2, s.Overview);
        Assert.True(outcome.DescriptionWritten);
        Assert.Equal(new[] { "cause=v1-to-v2" }, r.Journal.Details("DescriptionWritten"));
    }

    [Fact]
    public void OnPass_ExactV1_IsReplacedByV2_WithoutWaitingForGrace()
    {
        var r = new Rig();
        var s = r.Gateway.Add("1", "remove-si-lu=NON", "propager-lu=NON");
        s.Overview = HelpText.V1;
        r.Seen.TryMarkSeen("1");
        var outcome = r.Service.OnPass(r.Snapshot("1")); // une seule passe suffit, pas deux comme pour la grâce des étiquettes
        Assert.Equal(HelpText.V2, s.Overview);
        Assert.True(outcome.DescriptionWritten);
    }

    [Fact]
    public void ModifiedDescription_IsNeverTouched()
    {
        var r = new Rig();
        var s = r.Gateway.Add("1", "remove-si-lu=NON", "propager-lu=NON");
        s.Overview = "le propriétaire a écrit autre chose, même en partie identique à " + HelpText.V1;
        r.Seen.TryMarkSeen("1");
        var before = s.Overview;
        r.Service.OnPass(r.Snapshot("1"));
        Assert.Equal(before, s.Overview);
        Assert.Empty(r.Journal.Of("DescriptionWritten"));
    }

    [Fact]
    public void AlreadyV2_IsNeverTouchedAgain_NoLoop()
    {
        var r = new Rig();
        var s = r.Gateway.Add("1", "remove-si-lu=NON", "propager-lu=NON");
        s.Overview = HelpText.V2;
        r.Seen.TryMarkSeen("1");
        r.Service.OnPass(r.Snapshot("1"));
        r.Service.OnPass(r.Snapshot("1"));
        Assert.Equal(HelpText.V2, s.Overview);
        Assert.Empty(r.Journal.Of("DescriptionWritten"));
    }

    [Fact]
    public void EmptyDescription_StillPosesV1First_ThenV2OnceReplaced()
    {
        var r = new Rig();
        var s = r.Gateway.Add("1");
        r.Service.OnFirstDetection(r.Snapshot("1")); // pose V1 (description vide)
        Assert.Equal(HelpText.V1, s.Overview);
        r.Service.OnPass(r.Snapshot("1"));           // relu : égal à V1 -> remplacé par V2, sans grâce
        Assert.Equal(HelpText.V2, s.Overview);
    }

    [Fact]
    public void ReplacementDoesNotAffectTagGraceCounters()
    {
        var r = new Rig();
        var s = r.Gateway.Add("1", "remove-si-lu=NON"); // propager-lu absent : compteur de grâce en cours
        s.Overview = HelpText.V1;
        r.Seen.TryMarkSeen("1");
        var first = r.Service.OnPass(r.Snapshot("1"));
        Assert.Equal(HelpText.V2, s.Overview);           // description remplacée immédiatement
        Assert.Equal(1, first.PendingGrace);              // propager-lu reste en attente de grâce, indépendant
        Assert.DoesNotContain("propager-lu=NON", s.Tags);
    }

    [Fact]
    public void CaseOrWhitespaceDifference_IsNotAnExactMatch_NeverReplaced()
    {
        var r = new Rig();
        var s = r.Gateway.Add("1", "remove-si-lu=NON", "propager-lu=NON");
        s.Overview = HelpText.V1 + " "; // un seul caractère de différence
        r.Seen.TryMarkSeen("1");
        r.Service.OnPass(r.Snapshot("1"));
        Assert.Equal(HelpText.V1 + " ", s.Overview);
    }

    [Fact]
    public void GatewayApplyDefaults_ReplacesOnlyIfStillExactlyV1AtWriteTime()
    {
        // Test direct du port (OverviewChange), indépendant de DefaultsService : la ré-vérification se fait DANS
        // ApplyDefaults, au moment de l'écriture, jamais sur une valeur lue avant.
        var r = new Rig();
        var s = r.Gateway.Add("1");
        s.Overview = "le propriétaire a déjà écrit autre chose";
        var result = r.Gateway.ApplyDefaults("1", Array.Empty<MarkerFamily>(), new OverviewChange(HelpText.V1, HelpText.V2));
        Assert.False(result.OverviewWritten);
        Assert.Equal("le propriétaire a déjà écrit autre chose", s.Overview);

        s.Overview = HelpText.V1;
        result = r.Gateway.ApplyDefaults("1", Array.Empty<MarkerFamily>(), new OverviewChange(HelpText.V1, HelpText.V2));
        Assert.True(result.OverviewWritten);
        Assert.Equal(HelpText.V2, s.Overview);
    }
}
