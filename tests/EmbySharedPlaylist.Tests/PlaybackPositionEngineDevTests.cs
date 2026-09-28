using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Engine;
using EmbySharedPlaylist.Reconciliation;
using Xunit;

namespace EmbySharedPlaylist.Tests;

public class PlaybackPositionEngineDevTests
{
    private sealed class Rig
    {
        public readonly FakeGateway Gateway = new();
        public readonly FakeUserDataGateway UserData = new();
        public readonly PluginWriteTracker WriteTracker = new();
        public readonly SeenPlaylists Seen = new();
        public readonly PlaylistLocks Locks = new();
        public readonly ListJournal Journal = new();
        public readonly HandlerStats Handler = new();
        public readonly PlaybackPositionEngine Engine;

        public Rig()
        {
            var defaults = new DefaultsService(Gateway, Seen, Locks, Journal, "AIDE", () => 2, null, TimeSpan.FromMilliseconds(150));
            Engine = new PlaybackPositionEngine(Gateway, UserData, WriteTracker, defaults, Seen, Locks, Journal, new FakeClock(),
                TimeSpan.FromMilliseconds(150), handler: Handler);
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

    // ---- Matrice des marqueurs (propager-lu seule active la propagation de position) ------------------------

    [Theory]
    [InlineData("propager-lu=OUI", true)]
    [InlineData("propager-lu=NON", false)]
    [InlineData("propager-lu=OUI", "propager-lu=NON", false)] // OUI+NON : NON l'emporte
    public void OnlyPropagerLuOui_TriggersPropagation(params object[] args)
    {
        var tags = args.TakeWhile(a => a is string).Cast<string>().ToArray();
        var expected = (bool)args[^1];
        var r = new Rig();
        r.Add("1", tags, "m1");
        var result = r.Engine.Handle("u", "m1", 12345);
        Assert.Equal(expected, r.UserData.GetPosition("v", "m1") == 12345);
    }

    [Fact]
    public void NoMarker_IsLegacy_NoPropagation()
    {
        var r = new Rig();
        r.Add("1", Array.Empty<string>(), "m1");
        r.Engine.Handle("u", "m1", 5000);
        Assert.Equal(0L, r.UserData.GetPosition("v", "m1"));
        Assert.Equal(new[] { "inactive" }, r.Journal.Details("Skipped"));
    }

    [Fact]
    public void RemoveSiLu_IsNeverConsulted()
    {
        var r = new Rig();
        var s = r.Add("1", new[] { "remove-si-lu=OUI", "propager-lu=OUI" }, "m1"); // remove-si-lu actif ou pas : sans effet ici
        r.Engine.Handle("u", "m1", 999);
        Assert.Equal(999L, r.UserData.GetPosition("v", "m1"));
        Assert.Equal(new[] { "m1" }, s.Items); // l'engine de position ne retire jamais rien
    }

    // ---- Revue C1 : HandlerStats confondue avec le flux du lu (Diagnostics/State.Handler) ---------------------

    [Fact]
    public void Handle_RecordsItsDurationInHandlerStats_LikeThePlaybackProcessor()
    {
        var r = new Rig();
        r.Add("1", new[] { "propager-lu=OUI" }, "m1");
        Assert.Equal(0, r.Handler.Snapshot().Count);

        r.Engine.Handle("u", "m1", 100);

        Assert.Equal(1, r.Handler.Snapshot().Count);
    }

    [Fact]
    public void Handle_RecordsInHandlerStats_EvenOnTheErrorExitPath()
    {
        // Revue C1 : la mesure doit couvrir CHAQUE appel, quel que soit le chemin de sortie (pas seulement le cas nominal).
        var handler = new HandlerStats();
        var defaults = new DefaultsService(new ThrowingGateway(), new SeenPlaylists(), new PlaylistLocks(), new ListJournal(), "A", () => 2);
        var engine = new PlaybackPositionEngine(new ThrowingGateway(), new FakeUserDataGateway(), new PluginWriteTracker(), defaults,
            new SeenPlaylists(), new PlaylistLocks(), new ListJournal(), new FakeClock(), handler: handler);

        engine.Handle("u", "m1", 1);

        Assert.Equal(1, handler.Snapshot().Count);
    }

    [Fact]
    public void Handle_WithNoHandlerStatsProvided_NeverThrows()
    {
        var gateway = new FakeGateway();
        gateway.Add("1", "propager-lu=OUI").Members = new List<string> { "o", "u", "v" };
        var defaults = new DefaultsService(gateway, new SeenPlaylists(), new PlaylistLocks(), new ListJournal(), "A", () => 2);
        var engine = new PlaybackPositionEngine(gateway, new FakeUserDataGateway(), new PluginWriteTracker(), defaults,
            new SeenPlaylists(), new PlaylistLocks(), new ListJournal(), new FakeClock()); // handler omis (paramètre optionnel)
        var ex = Record.Exception(() => engine.Handle("u", "m1", 1));
        Assert.Null(ex);
    }

    // ---- Propagation, R8, "same-position" ------------------------------------------------------------------

    [Fact]
    public void ActiveMarker_PropagatesToOwnerAndOtherMember_JournalsOnce()
    {
        var r = new Rig();
        r.Add("1", new[] { "propager-lu=OUI" }, "m1"); // membres par défaut : "o" (propriétaire) et "v"
        var result = r.Engine.Handle("u", "m1", 42_000);

        Assert.Equal(42_000L, r.UserData.GetPosition("o", "m1"));
        Assert.Equal(42_000L, r.UserData.GetPosition("v", "m1"));
        Assert.Equal(1, result.PlaylistsChanged);
        Assert.Equal(2, result.Propagated);
        var entry = r.Journal.Of("PositionPropagation").Single();
        Assert.Equal("u", entry.UserId);
        Assert.Equal("m1", entry.ItemId);
        Assert.StartsWith("members=2 propagated=2 samePosition=0 noAccess=0 durationMs=", entry.Detail);
    }

    [Fact]
    public void R8_MemberWithoutAccess_IsSkipped_NoErrorNoWrite()
    {
        var r = new Rig();
        r.Add("1", new[] { "propager-lu=OUI" }, "m1");
        r.UserData.DenyAccess("o", "m1");
        r.UserData.DenyAccess("v", "m1");
        var result = r.Engine.Handle("u", "m1", 1000);
        Assert.Equal(0, r.UserData.SetPositionCalls);
        Assert.Empty(r.Journal.Of("Error"));
        Assert.Contains("noAccess=2", r.Journal.Of("PositionPropagation").Single().Detail);
        Assert.Equal(new[] { "no-access", "no-access" }, r.Journal.Details("Skipped"));
    }

    // ---- #30/#31 (v0.5.0) : confirmation explicite, aucun mecanisme special pour le proprietaire -------------

    [Fact]
    public void DeletedOwner_AsAPropagationTarget_IsSkippedLikeAnyMemberWithoutAccess_NoCrash()
    {
        // "o" reste dans MemberIds (le proprietaire est un membre comme un autre) mais son compte n'existe plus :
        // EmbyUserDataGateway.HasAccess renverrait faux (GetUserById ne trouve plus l'utilisateur), simule ici par
        // DenyAccess (R8, meme chemin que n'importe quel membre).
        var r = new Rig();
        r.Add("1", new[] { "propager-lu=OUI" }, "m1");
        r.UserData.DenyAccess("o", "m1"); // "o" = proprietaire supprime
        var ex = Record.Exception(() => r.Engine.Handle("u", "m1", 1000));
        Assert.Null(ex);
        Assert.Equal(1000L, r.UserData.GetPosition("v", "m1")); // l'autre membre recoit quand meme la propagation
        Assert.Contains("no-access", r.Journal.Details("Skipped"));
    }

    [Fact]
    public void ManagedPlaylistWithNoMedia_IsNeverACandidate_NoCrash()
    {
        var r = new Rig();
        r.Add("1", new[] { "propager-lu=OUI" }); // aucun media (params vide)
        var ex = Record.Exception(() => r.Engine.Handle("u", "m1", 1000));
        Assert.Null(ex);
        Assert.Empty(r.Journal.Of("PositionPropagation"));
    }

    [Fact]
    public void SamePosition_ProducesNoWrite_AndIsCounted()
    {
        var r = new Rig();
        r.Add("1", new[] { "propager-lu=OUI" }, "m1");
        r.UserData.SetPlayed("o", "m1", false); // sans effet, juste pour établir l'état
        r.UserData.SetPosition("o", "m1", 500); // déjà à cette position avant l'événement
        r.UserData.SetPosition("v", "m1", 500);
        var before = r.UserData.SetPositionCalls;

        r.Engine.Handle("u", "m1", 500);

        Assert.Equal(before, r.UserData.SetPositionCalls); // aucun NOUVEL appel d'écriture (déjà filtré avant même SetPosition)
        Assert.Contains("samePosition=2", r.Journal.Of("PositionPropagation").Single().Detail);
        Assert.Equal(new[] { "same-position", "same-position" }, r.Journal.Details("Skipped"));
    }

    [Fact]
    public void PluginWriteTracker_IsRegisteredOnlyWhenAWriteWillActuallyHappen()
    {
        // Revue A1 (v0.3.0) appliquée ici : ne jamais enregistrer une écriture qui ne surviendra pas (position déjà identique),
        // sinon l'entrée reste "pending" et pourrait mal classer une action réelle ultérieure du membre en écho.
        var r = new Rig();
        r.Add("1", new[] { "propager-lu=OUI" }, "m1");
        r.UserData.SetPosition("o", "m1", 500); // "o" déjà à la position cible

        r.Engine.Handle("u", "m1", 500);

        Assert.False(r.WriteTracker.TryConsume("o", "m1")); // jamais enregistré : aucune écriture prévue pour "o"
        Assert.True(r.WriteTracker.TryConsume("v", "m1"));  // enregistré pour "v" : une écriture a bien eu lieu
    }

    [Fact]
    public void ReadOnlyMember_PropagatesLikeAnyOtherMember_Q3()
    {
        var r = new Rig();
        var s = r.Add("1", new[] { "propager-lu=OUI" }, "m1");
        s.Members = new List<string> { "o", "u", "readonly" };
        r.Engine.Handle("u", "m1", 777);
        Assert.Equal(777L, r.UserData.GetPosition("readonly", "m1"));
    }

    [Fact]
    public void SourceUser_IsNeverWrittenByThisEngine()
    {
        var r = new Rig();
        r.Add("1", new[] { "propager-lu=OUI" }, "m1");
        r.Engine.Handle("u", "m1", 111);
        Assert.DoesNotContain(("u", "m1", 111L), r.UserData.PositionsSet);
    }

    // ---- Première détection, non-transitivité, budget, échec isolé -----------------------------------------

    [Fact]
    public void UnseenPlaylist_GetsFirstDetection_ThenPropagatesIfActive()
    {
        var r = new Rig();
        var s = r.Gateway.Add("1", "propager-lu=OUI");
        s.Members = new List<string> { "o", "u" };
        s.Items = new List<string> { "m1" };
        r.Engine.Handle("u", "m1", 300);
        Assert.Contains("remove-si-lu=NON", s.Tags); // pose de la famille absente
        Assert.Equal(300L, r.UserData.GetPosition("o", "m1"));
        Assert.True(r.Seen.IsSeen("1"));
    }

    [Fact]
    public void TwoPlaylists_APropagationOnOneDoesNotTouchTheOther()
    {
        var r = new Rig();
        var s1 = r.Add("1", new[] { "propager-lu=OUI" }, "shared-media");
        s1.Members = new List<string> { "o", "u", "member-of-1-only" };
        var s2 = r.Add("2", new[] { "propager-lu=OUI" }, "other-media");
        s2.Members = new List<string> { "o", "u", "member-of-2-only" };

        r.Engine.Handle("u", "shared-media", 1000);

        Assert.Equal(1000L, r.UserData.GetPosition("member-of-1-only", "shared-media"));
        Assert.Equal(0L, r.UserData.GetPosition("member-of-2-only", "shared-media"));
        Assert.Empty(r.Journal.Of("PositionPropagation").Where(e => e.PlaylistId == "2"));
    }

    [Fact]
    public void LockBusy_IsSkippedWithoutWriting()
    {
        var r = new Rig();
        var s = r.Add("1", new[] { "propager-lu=OUI" }, "m1");
        var held = new ManualResetEventSlim(); var release = new ManualResetEventSlim();
        var t = new Thread(() => { using var l = r.Locks.TryAcquire("1", TimeSpan.FromSeconds(5)); held.Set(); release.Wait(); });
        t.Start(); held.Wait();
        try
        {
            var result = r.Engine.Handle("u", "m1", 10);
            Assert.Equal(0L, r.UserData.GetPosition("o", "m1"));
            Assert.Contains("lock-busy", r.Journal.Details("Skipped"));
            Assert.Equal(1, result.Skipped);
        }
        finally { release.Set(); t.Join(); }
    }

    [Fact]
    public void AFailingSetPosition_IsIsolated_JournaledByTypeOnly_OtherMembersUnaffected()
    {
        var r = new Rig();
        var s = r.Add("1", new[] { "propager-lu=OUI" }, "m1");
        s.Members = new List<string> { "o", "u", "v", "w" };
        r.UserData.ThrowOnSetPositionFor = (uid, _) => uid == "v";
        var result = Record.Exception(() => r.Engine.Handle("u", "m1", 42));
        Assert.Null(result); // Handle ne lève jamais même si un membre échoue
    }

    [Fact]
    public void NeverThrows_EvenWhenTheGatewayCannotListTheCandidates()
    {
        var engine = new PlaybackPositionEngine(new ThrowingGateway(), new FakeUserDataGateway(), new PluginWriteTracker(),
            new DefaultsService(new ThrowingGateway(), new SeenPlaylists(), new PlaylistLocks(), new ListJournal(), "A", () => 2),
            new SeenPlaylists(), new PlaylistLocks(), new ListJournal(), new FakeClock());
        var result = Record.Exception(() => engine.Handle("u", "m1", 1));
        Assert.Null(result);
    }

    [Fact]
    public void OneLockAtATime_AndTheWriteRunsUnderTheLock()
    {
        var r = new Rig();
        r.Add("1", new[] { "propager-lu=OUI" }, "m1");
        r.Add("2", new[] { "propager-lu=OUI" }, "m1");
        var held = new List<string>();
        r.UserData.ThrowOnSetPositionFor = null;
        r.Engine.Handle("u", "m1", 5);
        // Le budget/verrou sont couverts par les tests de ReadRemovalEngine ; ici on vérifie juste que les deux playlists sont traitées.
        Assert.Equal(2, r.Journal.Of("PositionPropagation").Count());
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
