using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Engine;
using EmbySharedPlaylist.Reconciliation;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// Tests de SPÉCIFICATION du retrait du média lu (issue #12, critères d'acceptation ; issue #16), écrits comme la chaîne réelle
/// du <c>PlaybackListener</c> : événement UserDataSaved -> <see cref="PlayedTransitionTracker"/> -> <see cref="ReadRemovalEngine"/>.
/// Indépendants des tests boîte blanche <c>*DevTests</c> (dev-plugin) ; réutilisent leurs faux (<c>FakeGateway</c>, <c>ListJournal</c>, <c>FakeClock</c>).
/// Règles : un média qui passe de non lu à lu est retiré d'une playlist PARTAGÉE seulement si <c>remove-si-lu=OUI</c> est seule (NON l'emporte) ;
/// <c>propager-lu</c> n'est jamais consulté ; aucune écriture hors retrait ; le gestionnaire ne lève jamais.
/// </summary>
public class ReadRemovalEngineSpecTests
{
    private sealed class Flow
    {
        public readonly FakeGateway Gateway = new();
        public readonly SeenPlaylists Seen = new();
        public readonly PlaylistLocks Locks = new();
        public readonly ListJournal Journal = new();
        public readonly FakeClock Clock = new();
        public readonly PlayedTransitionTracker Tracker = new();
        public bool Suspended;
        public readonly DefaultsService Defaults;
        public readonly ReadRemovalEngine Engine;

        public Flow(TimeSpan? lockTimeout = null)
        {
            var timeout = lockTimeout ?? TimeSpan.FromMilliseconds(500);
            Defaults = new DefaultsService(Gateway, Seen, Locks, Journal, "AIDE", () => 2, Clock, timeout, () => Suspended);
            Engine = new ReadRemovalEngine(Gateway, Defaults, Seen, Locks, Journal, Clock, timeout, () => Suspended);
        }

        /// <summary>Playlist partagée : propriétaire « o », membres « m » (écriture) et « r » (lecture seule) ; description non vide par défaut.</summary>
        public FakeGateway.State Playlist(string id, string[] tags, string? overview, params string[] items)
        {
            var s = Gateway.Add(id, tags);
            s.Overview = overview;
            s.Items = items.ToList();
            s.Members = new List<string> { "o", "m", "r" };
            return s;
        }

        public FakeGateway.State Seenlist(string id, string[] tags, params string[] items)
        {
            var s = Playlist(id, tags, "déjà", items);
            Seen.TryMarkSeen(id);
            return s;
        }

        /// <summary>Événement UserDataSaved tel que le listener le traite ; null si le suiveur ne signale aucune transition.</summary>
        public RemovalResult? UserData(string user, string item, string? reason, bool played) =>
            Tracker.OnUserData(user, item, reason, played) ? Engine.Handle(user, item) : null;

        public IReadOnlyList<string> Kinds => Journal.Entries.Select(e => e.Kind).ToList();
    }

    private static string[] Tags(string csv) => csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // ---- États des étiquettes (legacy x4 + propager-lu) -------------------------------------------------------------

    [Theory]
    [InlineData("", false)]                                                       // aucune étiquette : legacy
    [InlineData("remove-si-lu=NON", false)]
    [InlineData("remove-si-lu=OUI,remove-si-lu=NON", false)]                      // NON l'emporte
    [InlineData("remove-si-lu=NON,remove-si-lu=OUI", false)]                      // dans l'autre ordre aussi
    [InlineData("propager-lu=OUI", false)]                                        // propager-lu seul : sans effet
    [InlineData("propager-lu=OUI,remove-si-lu=NON", false)]
    [InlineData("propager-lu=OUI,propager-lu=NON", false)]
    [InlineData("remove-si-lu=OUI", true)]                                        // actif
    [InlineData("REMOVE-SI-LU = oui", true)]                                      // casse et espaces tolérés
    [InlineData("remove-si-lu=OUI,propager-lu=NON", true)]                        // l'autre famille n'interfère pas
    [InlineData("propager-lu=OUI,propager-lu=NON,remove-si-lu=OUI", true)]
    [InlineData("remove-si-lu=OUI,famille,noel", true)]                           // étiquettes du propriétaire ignorées
    [InlineData("remove-si-lu-oui", false)]                                       // variantes voisines ignorées
    [InlineData("remove-si-lu=OUIX", false)]
    public void OnlyRemoveSiLuOuiAlone_RemovesTheMedia(string tagsCsv, bool removed)
    {
        var f = new Flow();
        var tags = Tags(tagsCsv);
        var s = f.Seenlist("p", tags, "x", "y");

        var result = f.UserData("m", "x", "TogglePlayed", true);

        Assert.NotNull(result);
        Assert.Equal(removed ? new[] { "y" } : new[] { "x", "y" }, s.Items);
        Assert.Equal(tags, s.Tags);                                   // le moteur n'écrit jamais d'étiquette sur une playlist déjà vue
        if (!removed) Assert.Equal(0, f.Gateway.RemoveCalls);         // legacy : aucun appel de retrait
        Assert.Single(f.Journal.Of("MarkerSeen"));
    }

    [Fact]
    public void MarkerSeen_ReportsTheStateOfTheRemoveFamilyOnly()
    {
        var f = new Flow();
        f.Seenlist("p", Tags("remove-si-lu=OUI,remove-si-lu=NON,propager-lu=OUI"), "x");
        f.UserData("m", "x", "TogglePlayed", true);
        Assert.Equal(new[] { "family=remove-si-lu state=Both" }, f.Journal.Details("MarkerSeen"));
        Assert.Equal(new[] { "inactive" }, f.Journal.Details("Skipped"));
    }

    // ---- Transition (Q1) --------------------------------------------------------------------------------------------

    [Fact]
    public void Q1_ReplayOfAnAlreadyPlayedMedia_RemovesNothing_ButUncheckThenCheckDoes()
    {
        var f = new Flow();
        var s = f.Seenlist("p", Tags("remove-si-lu=OUI"), "x", "y");

        Assert.Null(f.UserData("m", "x", "PlaybackStart", true));       // déjà lu : la lecture repart
        Assert.Null(f.UserData("m", "x", "PlaybackFinished", true));    // relu jusqu'au bout : aucune transition
        Assert.Equal(new[] { "x", "y" }, s.Items);

        Assert.Null(f.UserData("m", "x", "TogglePlayed", false));       // décocher
        var result = f.UserData("m", "x", "TogglePlayed", true);        // recocher : transition
        Assert.NotNull(result);
        Assert.Equal(1, result!.EntriesRemoved);
        Assert.Equal(new[] { "y" }, s.Items);
    }

    [Fact]
    public void ProgressWithPlayedTrue_TriggersOnce_ThenOnlyAnExplicitToggleDoes()
    {
        var f = new Flow();
        var s = f.Seenlist("p", Tags("remove-si-lu=OUI"), "x");

        Assert.NotNull(f.UserData("m", "x", "PlaybackProgress", true));
        Assert.Empty(s.Items);

        s.Items.Add("x");                                               // le propriétaire remet le média
        Assert.Null(f.UserData("m", "x", "PlaybackProgress", true));    // dernière valeur connue = lu : pas de transition
        Assert.Null(f.UserData("m", "x", "PlaybackFinished", true));
        Assert.Equal(new[] { "x" }, s.Items);

        Assert.NotNull(f.UserData("m", "x", "TogglePlayed", true));     // TogglePlayed=true : transition certaine
        Assert.Empty(s.Items);
    }

    [Fact]
    public void StoppedAtHalf_HasNoEffect()
    {
        var f = new Flow();
        var s = f.Seenlist("p", Tags("remove-si-lu=OUI"), "x");
        Assert.Null(f.UserData("m", "x", "PlaybackStart", false));
        Assert.Null(f.UserData("m", "x", "PlaybackProgress", false));
        Assert.Null(f.UserData("m", "x", "PlaybackFinished", false));   // arrêt à 47 % : played reste false
        Assert.Equal(new[] { "x" }, s.Items);
        Assert.Empty(f.Journal.Entries);
    }

    // ---- Doublons, membres, playlists -------------------------------------------------------------------------------

    [Fact]
    public void Duplicates_AreAllRemovedOneAtATime_OnASingleTransition()
    {
        var f = new Flow();
        var s = f.Seenlist("p", Tags("remove-si-lu=OUI"), "x", "y", "x", "x");

        var result = f.UserData("m", "x", "TogglePlayed", true);

        Assert.Equal(new[] { "y" }, s.Items);
        Assert.Equal(3, result!.EntriesRemoved);
        Assert.Equal(1, result.PlaylistsChanged);
        var removal = Assert.Single(f.Journal.Of("Removal"));
        Assert.StartsWith("entries=3 durationMs=", removal.Detail);
        Assert.Equal("m", removal.UserId);
        Assert.Equal("x", removal.ItemId);
        Assert.Equal("p", removal.PlaylistId);
    }

    [Fact]
    public void ReadOnlyMember_AlsoTriggersTheRemoval_Q3_AndNobodyElsesFlagIsTouched()
    {
        var f = new Flow();
        var s = f.Seenlist("p", Tags("remove-si-lu=OUI"), "x");
        var result = f.UserData("r", "x", "TogglePlayed", true);       // « r » = membre en lecture seule
        Assert.Equal(1, result!.EntriesRemoved);
        Assert.Empty(s.Items);
        // le moteur n'a aucun accès aux données de lecture : le port n'expose que la lecture des playlists et le retrait (R9)
        Assert.Equal(new[] { "Removal", "MarkerSeen" }.OrderBy(k => k), f.Kinds.OrderBy(k => k));
    }

    [Fact]
    public void NonMember_IsNotACandidate_NothingHappens()
    {
        var f = new Flow();
        var s = f.Seenlist("p", Tags("remove-si-lu=OUI"), "x");
        var result = f.UserData("stranger", "x", "TogglePlayed", true);
        Assert.Equal(0, result!.Candidates);
        Assert.Equal(new[] { "x" }, s.Items);
        Assert.Empty(f.Journal.Entries);
    }

    [Fact]
    public void PublicOrPrivatePlaylistWithoutExplicitShare_IsIgnored()
    {
        var f = new Flow();
        var s = f.Seenlist("p", Tags("remove-si-lu=OUI"), "x");
        s.Members = new List<string> { "o" };                          // propriétaire seul : privée, ou publique sans ligne de partage
        var result = f.UserData("o", "x", "TogglePlayed", true);
        Assert.Equal(0, result!.Candidates);
        Assert.Equal(new[] { "x" }, s.Items);
        Assert.Empty(f.Journal.Entries);
    }

    [Fact]
    public void PlaylistWithoutTheMedia_IsNotACandidate()
    {
        var f = new Flow();
        f.Seenlist("p", Tags("remove-si-lu=OUI"), "y");
        var result = f.UserData("m", "x", "TogglePlayed", true);
        Assert.Equal(0, result!.Candidates);
        Assert.Empty(f.Journal.Entries);
    }

    [Fact]
    public void SeveralPlaylists_OnlyTheActiveOnesLoseTheMedia_AndAFailureStopsNobody()
    {
        var f = new Flow();
        var a = f.Seenlist("a", Tags("remove-si-lu=OUI"), "x");
        var b = f.Seenlist("b", Tags("remove-si-lu=NON"), "x");
        var c = f.Seenlist("c", Tags("remove-si-lu=OUI"), "x");
        var d = f.Seenlist("d", Tags("remove-si-lu=OUI"), "x");
        f.Gateway.ThrowOnRemoveFor = id => id == "c";

        var result = f.UserData("m", "x", "TogglePlayed", true);

        Assert.Empty(a.Items);
        Assert.Empty(d.Items);
        Assert.Equal(new[] { "x" }, b.Items);
        Assert.Equal(new[] { "x" }, c.Items);
        Assert.Equal(4, result!.Candidates);
        Assert.Equal(2, result.PlaylistsChanged);
        Assert.Equal(2, result.EntriesRemoved);
        Assert.Equal(2, result.Skipped);
        Assert.Equal(new[] { "InvalidOperationException" }, f.Journal.Details("Error"));   // type seul, jamais le message
        Assert.DoesNotContain(f.Journal.Entries, e => (e.Detail ?? string.Empty).Contains("secret"));
    }

    // ---- Première détection, ordre du journal ------------------------------------------------------------------------

    [Fact]
    public void UnseenPlaylistWithoutTag_GetsTheFirstDetectionBeforeTheEvaluation_AndKeepsTheMedia()
    {
        var f = new Flow();
        var s = f.Playlist("p", Tags(""), null, "x");
        f.UserData("m", "x", "TogglePlayed", true);

        Assert.Equal(new[] { "remove-si-lu=NON", "propager-lu=NON" }, s.Tags);
        Assert.Equal(new[] { "x" }, s.Items);                          // NON posé AVANT l'évaluation : le média reste
        Assert.Equal(new[] { "MarkerPosed", "MarkerPosed", "DescriptionWritten", "MarkerSeen", "Skipped" }, f.Kinds);
    }

    [Fact]
    public void UnseenPlaylistWithOui_IsFirstDetectedThenRemoved_InThatOrder()
    {
        var f = new Flow();
        var s = f.Playlist("p", Tags("remove-si-lu=OUI"), null, "x");
        f.UserData("m", "x", "TogglePlayed", true);

        Assert.Equal(new[] { "remove-si-lu=OUI", "propager-lu=NON" }, s.Tags);   // seule la famille absente est posée
        Assert.Empty(s.Items);
        Assert.Equal(new[] { "MarkerPosed", "DescriptionWritten", "MarkerSeen", "Removal" }, f.Kinds);
    }

    [Fact]
    public void TheEngineNeverWritesTagsOrTheDescription_OnAnAlreadySeenPlaylist()
    {
        var f = new Flow();
        var s = f.Seenlist("p", Tags("remove-si-lu=OUI"), "x");
        s.Overview = null;                                              // description vide : réservée à la passe (grâce), pas au retrait
        f.UserData("m", "x", "TogglePlayed", true);
        Assert.Equal(0, f.Gateway.ApplyCalls);
        Assert.Null(s.Overview);
        Assert.Equal(new[] { "remove-si-lu=OUI" }, s.Tags);
    }

    // ---- Verrou, écho, robustesse, suspension ------------------------------------------------------------------------

    [Fact]
    public void LockBusy_IsJournaled_NothingIsRemoved()
    {
        var f = new Flow(TimeSpan.FromMilliseconds(100));
        var s = f.Seenlist("p", Tags("remove-si-lu=OUI"), "x");
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            using var gate = f.Locks.TryAcquire("p", TimeSpan.FromSeconds(2));   // le verrou est lié au fil : pris et libéré dans la même tâche
            held.Set();
            release.Wait(TimeSpan.FromSeconds(5));
        });
        Assert.True(held.Wait(TimeSpan.FromSeconds(5)));

        var result = f.UserData("m", "x", "TogglePlayed", true);
        release.Set();
        holder.Wait();

        Assert.Equal(new[] { "x" }, s.Items);
        Assert.Equal(0, f.Gateway.RemoveCalls);
        Assert.Contains("lock-busy", f.Journal.Details("Skipped"));
        Assert.Equal(1, result!.Skipped);
    }

    [Fact]
    public void EchoOfOurOwnWrites_IsIgnored_ReentrantThenAlreadySeen_NoSecondWrite()
    {
        var f = new Flow();
        var s = f.Playlist("p", Tags(""), null, "x");
        var coordinator = new FirstDetectionCoordinator(f.Gateway, f.Defaults, f.Seen, f.Journal, f.Clock, () => f.Suspended);

        using (WriteScope.Enter()) coordinator.OnPlaylistEvent("p");    // écho pendant une écriture du plugin
        Assert.Contains("reentrant", f.Journal.Details("Skipped"));
        Assert.False(f.Seen.IsSeen("p"));
        Assert.Equal(0, f.Gateway.ApplyCalls);

        coordinator.OnPlaylistEvent("p");                               // vraie première détection
        Assert.Equal(1, f.Gateway.ApplyCalls);
        Assert.True(f.Seen.IsSeen("p"));

        coordinator.OnPlaylistEvent("p");                               // écho de cette écriture : déjà vue
        Assert.Contains("already-seen", f.Journal.Details("Skipped"));
        Assert.Equal(1, f.Gateway.ApplyCalls);
        Assert.Equal(2, s.Tags.Count);
    }

    [Fact]
    public void TheHandlerNeverThrows_WhateverTheGatewayDoes()
    {
        var f = new Flow();
        f.Seenlist("p", Tags("remove-si-lu=OUI"), "x");
        f.Gateway.ThrowOnRemoveFor = _ => true;

        var error = Record.Exception(() => f.UserData("m", "x", "TogglePlayed", true));

        Assert.Null(error);
        Assert.All(f.Journal.Details("Error"), d => Assert.Equal("InvalidOperationException", d));
    }

    [Fact]
    public void ThreeUsersFinishingTheSameMediaAtOnce_RemoveItExactlyOnce()
    {
        var f = new Flow(TimeSpan.FromSeconds(2));
        var s = f.Seenlist("p", Tags("remove-si-lu=OUI"), "x");
        using var start = new ManualResetEventSlim();
        var tasks = new[] { "o", "m", "r" }.Select(u => Task.Run(() =>
        {
            start.Wait();
            return f.UserData(u, "x", "TogglePlayed", true);
        })).ToArray();
        start.Set();
        Task.WaitAll(tasks);

        Assert.Empty(s.Items);
        Assert.Single(f.Journal.Of("Removal"));                                  // un seul retrait effectif
        Assert.Equal(1, tasks.Sum(t => t.Result!.EntriesRemoved));
        Assert.Empty(f.Journal.Of("Error"));
    }

    [Fact]
    public void DifferentMediaFinishedAtOnceByThreeUsers_AreAllRemoved()
    {
        var f = new Flow(TimeSpan.FromSeconds(2));
        var s = f.Seenlist("p", Tags("remove-si-lu=OUI"), "a", "b", "c");
        using var start = new ManualResetEventSlim();
        var jobs = new[] { ("o", "a"), ("m", "b"), ("r", "c") };
        var tasks = jobs.Select(j => Task.Run(() =>
        {
            start.Wait();
            return f.UserData(j.Item1, j.Item2, "TogglePlayed", true);
        })).ToArray();
        start.Set();
        Task.WaitAll(tasks);

        Assert.Empty(s.Items);
        Assert.Equal(3, f.Journal.Of("Removal").Count());
        Assert.Empty(f.Journal.Of("Error"));
    }

    [Fact]
    public void WhenSuspended_TheChainDoesNothing_ThenResumes()
    {
        var f = new Flow();
        var s = f.Seenlist("p", Tags("remove-si-lu=OUI"), "x", "y");
        f.Suspended = true;                                             // EnableReentrancyProbe=true : le moteur se tait

        var result = f.UserData("m", "x", "TogglePlayed", true);

        Assert.Equal(new RemovalResult(0, 0, 0, 0, 0), result);
        Assert.Equal(new[] { "x", "y" }, s.Items);
        Assert.Equal(0, f.Gateway.GetCalls);
        Assert.Equal(0, f.Gateway.RemoveCalls);
        Assert.Empty(f.Journal.Entries);

        f.Suspended = false;
        Assert.NotNull(f.UserData("m", "y", "TogglePlayed", true));     // reprise : la transition suivante est traitée
        Assert.Equal(new[] { "x" }, s.Items);
    }
}
