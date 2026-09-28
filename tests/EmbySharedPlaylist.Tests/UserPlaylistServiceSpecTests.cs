using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Marker;
using EmbySharedPlaylist.Reconciliation;
using EmbySharedPlaylist.UserPage;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// Tests de SPÉCIFICATION de <c>EmbySharedPlaylist.UserPage.UserPlaylistService</c> (v1.1.0, #39), écrits AVANT le
/// code depuis le plan (`_work/reports/plan-20260928-143007.md`, tâches 3–5), les contrats
/// (`contracts/http-endpoints.md` « Page utilisateur ») et la spec (`docs/chronogrammes.md` D19/D20/S10).
///
/// <b>Contrat proposé pour dev-plugin</b> (le code livré peut différer dans le détail interne, mais doit satisfaire
/// exactement ces comportements) :
/// <list type="bullet">
/// <item><c>Core.IShareGateway</c> : <c>ListOwnedPlaylists(ownerId)</c>, <c>GetOwned(ownerId, playlistId)</c> (null
/// si non possédée/inexistante), <c>UpsertShare(playlistId, userId, level)</c>, <c>DeleteShare(playlistId, userId)</c>.
/// <c>Core.OwnedPlaylist(Id, Name, ItemCount, IReadOnlyList&lt;OwnedPlaylistMember&gt; Members, IReadOnlyList&lt;string&gt; Tags)</c>,
/// <c>IsShared =&gt; Members.Count &gt; 0</c>. <c>OwnedPlaylistMember(UserId, Level)</c>.</item>
/// <item><c>Core.IUserDirectory</c> : <c>ListSelectable(excludeUserId)</c> (actifs, hors excludeUserId),
/// <c>Find(userId)</c> (null si inconnu), <c>CanShare(userId)</c> (délègue à <see cref="IUserPolicyGateway.IsSharingEnabled"/>).
/// <c>DirectoryUser(UserId, Name, Active)</c>.</item>
/// <item><c>Core.IPlaylistGateway.ReplaceFamily(playlistId, family, enabled) -&gt; ReplaceFamilyResult(Tags, Removed)</c>
/// (D19, MarkerEditor + lecture-écriture-relecture, même patron qu'<c>ApplyDefaults</c>) ; méthode AJOUTÉE à
/// l'interface existante (le <c>FakeGateway</c> de <c>ReconciliationDevTests</c> devra l'implémenter, à charge de dev-plugin).</item>
/// <item><c>UserPage.UserPageErrors</c> (constantes <c>string</c>, mêmes chaînes que les contrats) :
/// <c>SharingDisabled</c>="sharing-disabled", <c>NotFound</c>="not-found", <c>InvalidLevel</c>="invalid-level",
/// <c>Self</c>="self", <c>InvalidUser</c>="invalid-user", <c>NotShared</c>="not-shared", <c>InvalidFamily</c>="invalid-family",
/// <c>Busy</c>="busy", <c>Internal</c>="internal" (dernier : jamais renvoyé par une règle métier, uniquement par le
/// garde-fou « ne lève jamais » ci-dessous).</item>
/// <item><c>UserPage.UserPageResult&lt;T&gt;(bool Ok, string? Error, T? Value)</c> : issue de chaque méthode du service.</item>
/// <item><c>UserPage.UserPageMemberDto(UserId, Name, Level)</c> (nom résolu via <c>IUserDirectory.Find</c>),
/// <c>UserPage.UserPagePlaylistDto(PlaylistId, Name, ItemCount, IsShared, IReadOnlyList&lt;UserPageMemberDto&gt; Members,
/// IReadOnlyDictionary&lt;string,string&gt; Options)</c> (clés Options = <c>MarkerEvaluator.FamilyName</c>, valeurs =
/// <c>MarkerState.ToString()</c> : "None"/"Non"/"Oui"/"Both"), <c>UserPage.UserPageSelectableDto(UserId, Name)</c>.</item>
/// <item><c>UserPage.UserPlaylistService(IShareGateway shares, IUserDirectory users, IPlaylistGateway playlists,
/// PlaylistLocks locks, DefaultsService defaults, IJournal journal, IClock? clock = null, TimeSpan? lockTimeout = null)</c> :
/// <list type="bullet">
/// <item><c>UserPageResult&lt;IReadOnlyList&lt;UserPagePlaylistDto&gt;&gt; ListOwned(string requesterId)</c></item>
/// <item><c>UserPageResult&lt;IReadOnlyList&lt;UserPageSelectableDto&gt;&gt; ListSelectableUsers(string requesterId)</c></item>
/// <item><c>UserPageResult&lt;UserPagePlaylistDto&gt; AddOrUpdateMember(string requesterId, string playlistId, string targetUserId, string level)</c></item>
/// <item><c>UserPageResult&lt;UserPagePlaylistDto&gt; RemoveMember(string requesterId, string playlistId, string targetUserId)</c></item>
/// <item><c>UserPageResult&lt;UserPagePlaylistDto&gt; SetOption(string requesterId, string playlistId, string family, bool enabled)</c></item>
/// </list>
/// Règles, dans cet ordre : <c>CanShare</c> (403 <c>sharing-disabled</c>) évaluée EN PREMIER, pour LES CINQ méthodes,
/// avant tout autre calcul (même une playlist inexistante ne doit jamais fuiter au travers) ; propriété — lecture
/// via <c>IShareGateway.GetOwned</c> — (404 <c>not-found</c>, indiscernable d'une playlist inexistante) ; niveaux
/// stricts <c>"Read"</c>/<c>"Write"</c> (comparaison EXACTE, sensible à la casse ; 400 <c>invalid-level</c>, jamais
/// Manage/ManageDelete/None) ; cible = soi (400 <c>self</c>) ; cible inconnue ou <c>Active=false</c> (400
/// <c>invalid-user</c>) ; verrou <see cref="PlaylistLocks"/> pris ENSUITE, avec le <c>lockTimeout</c> (défaut 5 s) →
/// 409 <c>busy</c> si non obtenu ; relecture SOUS le verrou avant toute écriture (anti-race, même discipline que
/// <see cref="DefaultsService.OnPass"/>) ; premier partage (playlist non partagée AVANT l'upsert) →
/// <see cref="DefaultsService.OnFirstDetection"/> appelée SOUS LE MÊME VERROU (réentrance, <see cref="PlaylistLocks"/>
/// est réentrant sur le même fil) ; options réservées aux playlists partagées (409 <c>not-shared</c>) ; famille hors
/// <c>{"remove-si-lu","propager-lu"}</c> (400 <c>invalid-family</c>) ; journal <c>ShareChanged</c> (ids seulement,
/// <c>Detail="action=add|update|remove level=Read|Write|-"</c>) et <c>MarkerSet</c>
/// (<c>Detail="family=&lt;f&gt; value=OUI|NON removed=&lt;n&gt;"</c>) ; ne lève JAMAIS (toute exception inattendue d'un
/// port est capturée, journalisée en <c>Error</c>, retournée comme <c>UserPageErrors.Internal</c>) ; le retour
/// (succès) est TOUJOURS la playlist relue après écriture (source de vérité serveur, jamais l'état avant écriture).</item>
/// </list>
/// Réutilise les faux partagés de <c>ReconciliationDevTests</c> (<see cref="FakeClock"/>, <see cref="ListJournal"/>)
/// et un vrai <see cref="PlaylistLocks"/>/<see cref="SeenPlaylists"/>/<see cref="DefaultsService"/> (même discipline
/// que <c>ReadRemovalEngineSpecTests</c> : la chaîne réelle, pas un double du composant testé).
/// </summary>
public class UserPlaylistServiceSpecTests
{
    private const string Owner = "u1";
    private const string MemberWrite = "u2";
    private const string MemberRead = "u3";
    private const string Stranger = "u4"; // ne possède rien, cible d'IDOR

    /// <summary>Faux minimal d'<see cref="IPlaylistGateway"/> pour ce fichier (distinct du <c>FakeGateway</c> de
    /// <c>ReconciliationDevTests</c> : n'implémente que ce dont <see cref="DefaultsService"/> et
    /// <see cref="UserPlaylistService"/> ont besoin, avec <c>ReplaceFamily</c>, D19).</summary>
    private sealed class FakeUserPageGateway : IPlaylistGateway
    {
        public sealed class State
        {
            public string? Owner;
            public List<string> Members = new();
            public List<string> Tags = new();
            public string? Overview;
        }

        public readonly Dictionary<string, State> Playlists = new();
        public Func<string, bool>? ThrowOnReplaceFamilyFor;
        public int ReplaceFamilyCalls;

        public State Add(string id, string owner, params string[] tags)
        {
            var s = new State { Owner = owner, Tags = tags.ToList(), Members = new List<string> { owner } };
            Playlists[id] = s;
            return s;
        }

        private PlaylistSnapshot Snap(string id, State s) => new(id, s.Owner, s.Members.ToList(), s.Tags.ToList(), s.Overview);

        public IReadOnlyList<PlaylistSnapshot> ListSharedPlaylists() =>
            Playlists.Select(p => Snap(p.Key, p.Value)).Where(s => s.IsShared).ToList();

        public IReadOnlyList<PlaylistSnapshot> ListSharedPlaylistsOfUserContaining(string userId, string itemId) =>
            Array.Empty<PlaylistSnapshot>();

        public PlaylistSnapshot? Get(string playlistId) =>
            Playlists.TryGetValue(playlistId, out var s) ? Snap(playlistId, s) : null;

        public bool RemoveOneEntry(string playlistId, string itemId) => false;

        public ApplyResult ApplyDefaults(string playlistId, IReadOnlyList<MarkerFamily> familiesToPose, OverviewChange? overview)
        {
            var s = Playlists[playlistId];
            var posed = new List<MarkerFamily>();
            foreach (var f in familiesToPose)
            {
                if (MarkerEvaluator.Evaluate(s.Tags, f) != MarkerState.None) continue;
                s.Tags.Add(MarkerEvaluator.NonTag(f));
                posed.Add(f);
            }
            var wrote = false;
            if (overview != null && (overview.RequiredCurrent == null ? string.IsNullOrWhiteSpace(s.Overview) : s.Overview == overview.RequiredCurrent))
            {
                s.Overview = overview.NewValue;
                wrote = true;
            }
            return new ApplyResult(posed, wrote);
        }

        public ReplaceFamilyResult ReplaceFamily(string playlistId, MarkerFamily family, bool enabled)
        {
            Interlocked.Increment(ref ReplaceFamilyCalls);
            if (ThrowOnReplaceFamilyFor?.Invoke(playlistId) == true) throw new InvalidOperationException("écriture en échec avec un message secret");
            var s = Playlists[playlistId];
            var (newTags, removed) = MarkerEditor.Replace(s.Tags, family, enabled);
            s.Tags = newTags.ToList();
            return new ReplaceFamilyResult(newTags, removed);
        }
    }

    private sealed class FakeShareGateway : IShareGateway
    {
        private readonly FakeUserPageGateway _playlists;
        public readonly Dictionary<string, List<OwnedPlaylistMember>> MembersByPlaylist = new();
        public readonly Dictionary<string, string> NamesByPlaylist = new();
        public readonly Dictionary<string, int> ItemCountByPlaylist = new();
        public int UpsertCalls;
        public int DeleteCalls;

        /// <summary>Sécurité (audit 20260928-154256 point 7, review 20260928-154519 "retour bool jamais vérifié") :
        /// quand faux, <see cref="DeleteShare"/> se comporte comme le port RÉEL le ferait sur un échec silencieux
        /// (rien n'est retiré, retourne faux) — sans exception, donc invisible au <c>catch</c> générique du service.</summary>
        public bool ForceDeleteShareResult = true;

        /// <summary>Simule la perte de la ligne <c>ManageDelete</c> du propriétaire APRÈS un <see cref="DeleteShare"/>
        /// réussi (EmbyShareGateway.DeleteShare : purge totale PUIS reconstruction non transactionnelle — un échec
        /// entre les deux appels SDK perd la ligne du propriétaire, cf. security-audit point 7). Pas un mock
        /// artificiel : reproduit exactement ce que <see cref="GetOwned"/> renverrait réellement dans ce cas
        /// (indiscernable d'une playlist non possédée, comme le fait <c>EmbyShareGateway.ToOwned</c>).</summary>
        public bool SimulateOwnerRowLostAfterDelete;

        private readonly HashSet<string> _ownerRowLost = new();

        public FakeShareGateway(FakeUserPageGateway playlists) => _playlists = playlists;

        public string Add(string id, string owner, string name, int itemCount, params (string UserId, string Level)[] members)
        {
            _playlists.Add(id, owner);
            NamesByPlaylist[id] = name;
            ItemCountByPlaylist[id] = itemCount;
            MembersByPlaylist[id] = members.Select(m => new OwnedPlaylistMember(m.UserId, m.Level)).ToList();
            return id;
        }

        public IReadOnlyList<OwnedPlaylist> ListOwnedPlaylists(string ownerId) =>
            _playlists.Playlists.Where(p => p.Value.Owner == ownerId)
                .Select(p => ToOwned(p.Key)).Where(o => o != null).Select(o => o!).ToList();

        public OwnedPlaylist? GetOwned(string ownerId, string playlistId)
        {
            if (!_playlists.Playlists.TryGetValue(playlistId, out var s) || s.Owner != ownerId) return null;
            return ToOwned(playlistId);
        }

        private OwnedPlaylist? ToOwned(string playlistId)
        {
            if (_ownerRowLost.Contains(playlistId)) return null; // ligne ManageDelete perdue : indiscernable d'une playlist non possédée (anti-IDOR, même comportement que le port réel)
            if (!_playlists.Playlists.TryGetValue(playlistId, out var s)) return null;
            var members = MembersByPlaylist.TryGetValue(playlistId, out var m) ? m : new List<OwnedPlaylistMember>();
            return new OwnedPlaylist(playlistId, NamesByPlaylist.GetValueOrDefault(playlistId, playlistId),
                ItemCountByPlaylist.GetValueOrDefault(playlistId, 0), members, s.Tags.ToList());
        }

        public bool UpsertShare(string playlistId, string userId, string level)
        {
            Interlocked.Increment(ref UpsertCalls);
            var list = MembersByPlaylist.TryGetValue(playlistId, out var m) ? m : MembersByPlaylist[playlistId] = new List<OwnedPlaylistMember>();
            list.RemoveAll(x => x.UserId == userId);
            list.Add(new OwnedPlaylistMember(userId, level));
            return true;
        }

        public bool DeleteShare(string playlistId, string userId)
        {
            Interlocked.Increment(ref DeleteCalls);
            if (!ForceDeleteShareResult) return false; // échec silencieux du port : AUCUNE mutation (comme un membre déjà absent côté SDK)
            var list = MembersByPlaylist.TryGetValue(playlistId, out var m) ? m : new List<OwnedPlaylistMember>();
            var removed = list.RemoveAll(x => x.UserId == userId) > 0;
            if (removed && SimulateOwnerRowLostAfterDelete) _ownerRowLost.Add(playlistId);
            return removed;
        }
    }

    private sealed class FakeUserDirectory : IUserDirectory
    {
        private readonly Dictionary<string, DirectoryUser> _users = new();
        private readonly HashSet<string> _canShare = new();

        public void Add(string userId, string name, bool active = true, bool canShare = false)
        {
            _users[userId] = new DirectoryUser(userId, name, active);
            if (canShare) _canShare.Add(userId);
        }

        public IReadOnlyList<DirectoryUser> ListSelectable(string excludeUserId) =>
            _users.Values.Where(u => u.Active && u.UserId != excludeUserId).OrderBy(u => u.Name, StringComparer.Ordinal).ToList();

        public DirectoryUser? Find(string userId) => _users.TryGetValue(userId, out var u) ? u : null;

        public bool CanShare(string userId) => _canShare.Contains(userId);
    }

    private sealed class Rig
    {
        public readonly FakeUserPageGateway Playlists = new();
        public readonly FakeShareGateway Shares;
        public readonly FakeUserDirectory Users = new();
        public readonly PlaylistLocks Locks = new();
        public readonly SeenPlaylists Seen = new();
        public readonly ListJournal Journal = new();
        public readonly FakeClock Clock = new();
        public readonly DefaultsService Defaults;
        public readonly UserPlaylistService Service;

        public Rig()
        {
            Shares = new FakeShareGateway(Playlists);
            Defaults = new DefaultsService(Playlists, Seen, Locks, Journal, "AIDE", () => 2, Clock, TimeSpan.FromMilliseconds(500));
            Service = new UserPlaylistService(Shares, Users, Playlists, Locks, Defaults, Journal, Clock, TimeSpan.FromMilliseconds(500));
            Users.Add(Owner, "Alice", canShare: true);
            Users.Add(MemberWrite, "Bob", canShare: false);
            Users.Add(MemberRead, "Carla", canShare: false);
            Users.Add(Stranger, "Dan", canShare: true);
        }
    }

    // ------------------------------------------------------------------ CA1/matrice d'autorisation (403/404/400)

    [Fact]
    public void ListOwned_WithoutPermission_ReturnsSharingDisabled_ForEverySuchEndpoint()
    {
        var r = new Rig();
        r.Users.Add("nope", "NoPerm", canShare: false);
        Assert.Equal(UserPageErrors.SharingDisabled, r.Service.ListOwned("nope").Error);
        Assert.Equal(UserPageErrors.SharingDisabled, r.Service.ListSelectableUsers("nope").Error);
        Assert.Equal(UserPageErrors.SharingDisabled, r.Service.AddOrUpdateMember("nope", "p", MemberWrite, "Write").Error);
        Assert.Equal(UserPageErrors.SharingDisabled, r.Service.RemoveMember("nope", "p", MemberWrite).Error);
        Assert.Equal(UserPageErrors.SharingDisabled, r.Service.SetOption("nope", "p", "remove-si-lu", true).Error);
    }

    [Fact]
    public void OtherUsersPlaylist_AndNonExistentPlaylist_ReturnTheSameNotFound_AntiIdor()
    {
        var r = new Rig();
        r.Shares.Add("p1", Owner, "À voir", 3);
        // p1 existe mais appartient à Owner : Stranger la cible -> not-found (jamais un 403 ou un 400 qui révélerait l'existence).
        var idor = r.Service.AddOrUpdateMember(Stranger, "p1", MemberWrite, "Write");
        var ghost = r.Service.AddOrUpdateMember(Stranger, "does-not-exist", MemberWrite, "Write");
        Assert.Equal(UserPageErrors.NotFound, idor.Error);
        Assert.Equal(UserPageErrors.NotFound, ghost.Error);
    }

    [Theory]
    [InlineData("Manage")]
    [InlineData("ManageDelete")]
    [InlineData("None")]
    [InlineData("write")] // casse stricte : "write" minuscule refusé (seuls "Read"/"Write" exacts)
    [InlineData("banana")]
    public void AddOrUpdateMember_RejectsAnyLevelOtherThanReadOrWrite_CA6(string level)
    {
        var r = new Rig();
        r.Shares.Add("p1", Owner, "À voir", 3);
        var result = r.Service.AddOrUpdateMember(Owner, "p1", MemberWrite, level);
        Assert.Equal(UserPageErrors.InvalidLevel, result.Error);
        Assert.Equal(0, r.Shares.UpsertCalls);
    }

    [Fact]
    public void AddOrUpdateMember_TargetingSelf_IsRefused()
    {
        var r = new Rig();
        r.Shares.Add("p1", Owner, "À voir", 3);
        Assert.Equal(UserPageErrors.Self, r.Service.AddOrUpdateMember(Owner, "p1", Owner, "Write").Error);
    }

    [Theory]
    [InlineData("unknown-user")]
    public void AddOrUpdateMember_UnknownTarget_IsRefused(string target)
    {
        var r = new Rig();
        r.Shares.Add("p1", Owner, "À voir", 3);
        Assert.Equal(UserPageErrors.InvalidUser, r.Service.AddOrUpdateMember(Owner, "p1", target, "Write").Error);
    }

    [Fact]
    public void AddOrUpdateMember_DisabledTarget_IsRefused()
    {
        var r = new Rig();
        r.Shares.Add("p1", Owner, "À voir", 3);
        r.Users.Add("disabled1", "Ancien compte", active: false);
        Assert.Equal(UserPageErrors.InvalidUser, r.Service.AddOrUpdateMember(Owner, "p1", "disabled1", "Write").Error);
    }

    [Fact]
    public void RemoveMember_NonMember_ReturnsNotFound()
    {
        var r = new Rig();
        r.Shares.Add("p1", Owner, "À voir", 3, (MemberWrite, "Write"));
        Assert.Equal(UserPageErrors.NotFound, r.Service.RemoveMember(Owner, "p1", MemberRead).Error);
    }

    [Fact]
    public void RemoveMember_OwnerRowIsNeverRemovable_Self()
    {
        var r = new Rig();
        r.Shares.Add("p1", Owner, "À voir", 3, (MemberWrite, "Write"));
        Assert.Equal(UserPageErrors.Self, r.Service.RemoveMember(Owner, "p1", Owner).Error);
    }

    [Fact]
    public void SetOption_OnANotYetSharedPlaylist_ReturnsNotShared()
    {
        var r = new Rig();
        r.Shares.Add("p1", Owner, "À voir", 3); // aucun membre : non partagée
        var result = r.Service.SetOption(Owner, "p1", "remove-si-lu", true);
        Assert.Equal(UserPageErrors.NotShared, result.Error);
    }

    [Theory]
    [InlineData("Remove-Si-Lu")]
    [InlineData("inconnue")]
    [InlineData("")]
    public void SetOption_InvalidFamily_IsRefused(string family)
    {
        var r = new Rig();
        r.Shares.Add("p1", Owner, "À voir", 3, (MemberWrite, "Write"));
        Assert.Equal(UserPageErrors.InvalidFamily, r.Service.SetOption(Owner, "p1", family, true).Error);
    }

    [Fact]
    public void Locked_ByAnotherThread_ReturnsBusy_WithinFiveSecondBudget()
    {
        var r = new Rig();
        r.Shares.Add("p1", Owner, "À voir", 3, (MemberWrite, "Write"));
        var holder = Task.Run(() =>
        {
            using var gate = r.Locks.TryAcquire("p1", TimeSpan.FromSeconds(2));
            Thread.Sleep(800);
        });
        Thread.Sleep(100); // laisse le holder prendre le verrou en premier
        var result = r.Service.SetOption(Owner, "p1", "remove-si-lu", true);
        holder.Wait();
        Assert.Equal(UserPageErrors.Busy, result.Error);
    }

    // ------------------------------------------------------------------ CA3/CA4 : premier partage, orchestration

    [Fact]
    public void FirstShare_OnAnUnsharedPlaylist_TriggersImmediateFirstDetection_S10Step1()
    {
        var r = new Rig();
        r.Shares.Add("p1", Owner, "À voir", 3); // pas encore partagée, aucune étiquette

        var result = r.Service.AddOrUpdateMember(Owner, "p1", MemberWrite, "Write");

        Assert.True(result.Ok);
        Assert.True(result.Value!.IsShared);
        // La 1re détection pose remove-si-lu=NON/propager-lu=NON de façon SYNCHRONE (CA4 : pas d'attente de la passe) :
        // la playlist relue reflète donc déjà l'état "Non" (inactif), pas "None" (aucune étiquette).
        Assert.Equal("Non", result.Value.Options["remove-si-lu"]);
        Assert.Equal("Non", result.Value.Options["propager-lu"]);
        var tags = r.Playlists.Playlists["p1"].Tags;
        Assert.Contains("remove-si-lu=NON", tags);
        Assert.Contains("propager-lu=NON", tags);
        Assert.Contains(r.Journal.Of("MarkerPosed"), e => e.PlaylistId == "p1");
        Assert.Single(r.Journal.Of("ShareChanged"));
    }

    [Fact]
    public void SharingAnAlreadySharedPlaylist_DoesNotTriggerASecondFirstDetection()
    {
        var r = new Rig();
        r.Shares.Add("p1", Owner, "À voir", 3, (MemberWrite, "Write"));
        r.Playlists.Playlists["p1"].Tags.AddRange(new[] { "remove-si-lu=NON", "propager-lu=NON" });
        r.Seen.TryMarkSeen("p1"); // déjà vue (première détection déjà faite lors du 1er partage)

        r.Service.AddOrUpdateMember(Owner, "p1", MemberRead, "Read");

        Assert.Empty(r.Journal.Of("MarkerPosed")); // aucune nouvelle pose : la playlist était déjà gérée
        Assert.Equal(1, r.Journal.Of("ShareChanged").Count(e => e.Detail != null && e.Detail.Contains("add")));
    }

    [Fact]
    public void RemovingTheLastMember_MakesThePlaylistUnshared_TagsRemainInert_S10Step6()
    {
        var r = new Rig();
        r.Shares.Add("p1", Owner, "À voir", 3, (MemberWrite, "Write"));
        r.Playlists.Playlists["p1"].Tags.AddRange(new[] { "remove-si-lu=OUI", "propager-lu=NON" });

        var result = r.Service.RemoveMember(Owner, "p1", MemberWrite);

        Assert.True(result.Ok);
        Assert.False(result.Value!.IsShared);
        Assert.Contains("remove-si-lu=OUI", r.Playlists.Playlists["p1"].Tags); // jamais supprimée par le retrait d'un membre
        Assert.Contains(r.Journal.Of("ShareChanged"), e => e.Detail != null && e.Detail.Contains("remove"));
    }

    [Fact]
    public void Members_NeverExposeTheOwnerRow()
    {
        var r = new Rig();
        r.Shares.Add("p1", Owner, "À voir", 3, (MemberWrite, "Write"), (MemberRead, "Read"));
        var list = r.Service.ListOwned(Owner);
        var dto = Assert.Single(list.Value!, p => p.PlaylistId == "p1");
        Assert.DoesNotContain(dto.Members, m => m.UserId == Owner);
        Assert.Equal(2, dto.Members.Count);
    }

    // ------------------------------------------------------------------ CA5/D19 : bascule d'option

    [Fact]
    public void SetOption_Enable_ReplacesFamilyAtomically_JournalsMarkerSet()
    {
        var r = new Rig();
        r.Shares.Add("p1", Owner, "À voir", 3, (MemberWrite, "Write"));
        r.Playlists.Playlists["p1"].Tags.AddRange(new[] { "remove-si-lu=NON", "propager-lu=NON", "favori" });

        var result = r.Service.SetOption(Owner, "p1", "remove-si-lu", true);

        Assert.True(result.Ok);
        Assert.Equal("Oui", result.Value!.Options["remove-si-lu"]);
        var tags = r.Playlists.Playlists["p1"].Tags;
        Assert.Single(tags, t => t.StartsWith("remove-si-lu", StringComparison.Ordinal));
        Assert.Contains("favori", tags); // étiquette étrangère intacte
        var entry = Assert.Single(r.Journal.Of("MarkerSet"));
        Assert.Equal("p1", entry.PlaylistId);
        Assert.Equal(Owner, entry.UserId);
        Assert.Contains("family=remove-si-lu", entry.Detail);
        Assert.Contains("value=OUI", entry.Detail);
        Assert.Contains("removed=1", entry.Detail);
    }

    [Fact]
    public void SetOption_ResolvesAnExistingConflict_Removed2_S10Step3()
    {
        var r = new Rig();
        r.Shares.Add("p1", Owner, "À voir", 3, (MemberWrite, "Write"));
        r.Playlists.Playlists["p1"].Tags.AddRange(new[] { "remove-si-lu=OUI", "remove-si-lu=NON" }); // édition manuelle en conflit

        var result = r.Service.SetOption(Owner, "p1", "remove-si-lu", true);

        Assert.True(result.Ok);
        Assert.Equal("Oui", result.Value!.Options["remove-si-lu"]);
        var entry = Assert.Single(r.Journal.Of("MarkerSet"));
        Assert.Contains("removed=2", entry.Detail);
    }

    [Fact]
    public void SetOption_NeverDeletesTheOtherFamily()
    {
        var r = new Rig();
        r.Shares.Add("p1", Owner, "À voir", 3, (MemberWrite, "Write"));
        r.Playlists.Playlists["p1"].Tags.AddRange(new[] { "remove-si-lu=NON", "propager-lu=OUI" });

        r.Service.SetOption(Owner, "p1", "remove-si-lu", true);

        Assert.Contains("propager-lu=OUI", r.Playlists.Playlists["p1"].Tags);
    }

    // ------------------------------------------------------------------ CA9 : journal ShareChanged/MarkerSet (ids seulement)

    [Fact]
    public void ShareChanged_NeverContainsNamesOrExtraFields_IdsAndActionOnly()
    {
        var r = new Rig();
        r.Shares.Add("p1", Owner, "À voir", 3);
        r.Service.AddOrUpdateMember(Owner, "p1", MemberWrite, "Write");
        var entry = Assert.Single(r.Journal.Of("ShareChanged"));
        Assert.Equal("p1", entry.PlaylistId);
        Assert.Equal(MemberWrite, entry.UserId);
        Assert.DoesNotContain("Bob", entry.Detail); // jamais de nom, ids seulement (contrat Diagnostics)
        Assert.DoesNotContain("Alice", entry.Detail);
        Assert.Contains("action=add", entry.Detail);
        Assert.Contains("level=Write", entry.Detail);
    }

    [Fact]
    public void AddOrUpdateMember_ChangingLevelOfAnExistingMember_JournalsUpdate_NotAdd()
    {
        var r = new Rig();
        r.Shares.Add("p1", Owner, "À voir", 3, (MemberRead, "Read"));
        r.Playlists.Playlists["p1"].Tags.AddRange(new[] { "remove-si-lu=NON", "propager-lu=NON" });
        r.Seen.TryMarkSeen("p1");

        r.Service.AddOrUpdateMember(Owner, "p1", MemberRead, "Write");

        var entry = Assert.Single(r.Journal.Of("ShareChanged"));
        Assert.Contains("action=update", entry.Detail);
        Assert.Contains("level=Write", entry.Detail);
    }

    // ------------------------------------------------------------------ Discipline « ne lève jamais »

    [Fact]
    public void UnexpectedGatewayException_IsNeverThrown_JournaledAsError_ReturnsInternal()
    {
        var r = new Rig();
        r.Shares.Add("p1", Owner, "À voir", 3, (MemberWrite, "Write"));
        r.Playlists.Playlists["p1"].Tags.AddRange(new[] { "remove-si-lu=NON" });
        r.Playlists.ThrowOnReplaceFamilyFor = _ => true;

        UserPageResult<UserPagePlaylistDto>? result = null;
        var ex = Record.Exception(() => result = r.Service.SetOption(Owner, "p1", "remove-si-lu", true));

        Assert.Null(ex);
        Assert.False(result!.Ok);
        Assert.Equal(UserPageErrors.Internal, result.Error);
        Assert.NotEmpty(r.Journal.Of("Error"));
    }

    // ------------------------------------------------------------------ GET User/Playlists, GET User/Users (forme)

    [Fact]
    public void ListOwned_OnlyPlaylistsOwnedByTheRequester_SortedByName()
    {
        var r = new Rig();
        r.Shares.Add("p2", Owner, "Zebra", 1);
        r.Shares.Add("p1", Owner, "Alpha", 2);
        r.Shares.Add("pOther", MemberWrite, "Pas à moi", 5); // appartient à un autre : jamais listée

        var list = r.Service.ListOwned(Owner).Value!;

        Assert.Equal(new[] { "Alpha", "Zebra" }, list.Select(p => p.Name));
    }

    [Fact]
    public void ListSelectableUsers_ExcludesRequesterAndDisabled_NoExtraField()
    {
        var r = new Rig();
        r.Users.Add("disabled1", "Zorro désactivé", active: false);
        var list = r.Service.ListSelectableUsers(Owner).Value!;
        Assert.DoesNotContain(list, u => u.UserId == Owner);
        Assert.DoesNotContain(list, u => u.UserId == "disabled1");
        Assert.Contains(list, u => u.UserId == MemberWrite);
    }

    // ------------------------------------------------------------------ Sécurité — point 7 (MOYENNE) de
    // _work/reports/security-audit-20260928-154256.md + "retour bool jamais vérifié" de
    // _work/reports/code-review-20260928-154519.md : ajoutés APRÈS la livraison réelle (2a6b383) suite à la
    // demande explicite du teamleader (audit + review). EmbyShareGateway.DeleteShare (Emby/EmbyShareGateway.cs:98)
    // purge PUIS reconstruit les lignes de partage sans transaction SDK ; UserPlaylistService.RemoveMember
    // (UserPage/UserPlaylistService.cs:182) appelle _shares.DeleteShare(...) SANS vérifier son retour bool.
    // dev-plugin corrige en parallèle (relecture de vérification + journal dédié "OwnerLost"). Les deux tests
    // ci-dessous sont ROUGES sur le code actuellement livré (2a6b383) et documentent le comportement ATTENDU
    // après ce correctif — à ne PAS "réparer" en affaiblissant l'assertion, mais en corrigeant UserPlaylistService.
    // Le cas "process tué entre les deux appels SDK" (la vraie cause racine) reste, lui, non reproductible par un
    // test (ni unitaire ni intégration) : aucune information n'existe côté C# pour le distinguer d'une écriture
    // réussie tant qu'aucune exception n'est levée — seule la RELECTURE après coup peut le détecter, ce que ces
    // tests vérifient.

    [Fact]
    public void RemoveMember_WhenThePortSignalsFailure_MustNeverReportSuccess()
    {
        // Le bool de retour de IShareGateway.DeleteShare EST le seul signal disponible pour un échec silencieux du
        // port (aucune exception : cf. contrat "Faux si le membre n'existait pas" et, plus largement, tout cas où
        // rien n'a été réellement écrit). Aujourd'hui ce bool est ignoré (code-review) : ce test échoue tant que
        // UserPlaylistService.RemoveMember ne le vérifie pas avant de journaliser ShareChanged/retourner un succès.
        var r = new Rig();
        r.Shares.Add("p1", Owner, "À voir", 3, (MemberWrite, "Write"));
        r.Shares.ForceDeleteShareResult = false; // le port échoue silencieusement : AUCUNE écriture n'a eu lieu

        var result = r.Service.RemoveMember(Owner, "p1", MemberWrite);

        Assert.False(result.Ok, "RemoveMember ne doit jamais rapporter un succès si le port n'a rien écrit (retour bool vérifié)");
    }

    [Fact]
    public void RemoveMember_WhenThePortSignalsFailure_NeverJournalsShareChanged()
    {
        // Corollaire du test précédent, ciblé sur le journal : un ShareChanged "action=remove" journalisé alors que
        // rien n'a été retiré serait un MENSONGE d'audit (le journal est la seule trace pour un administrateur).
        var r = new Rig();
        r.Shares.Add("p1", Owner, "À voir", 3, (MemberWrite, "Write"));
        r.Shares.ForceDeleteShareResult = false;

        r.Service.RemoveMember(Owner, "p1", MemberWrite);

        Assert.Empty(r.Journal.Of("ShareChanged"));
    }

    [Fact]
    public void RemoveMember_WhenTheOwnersManageDeleteRowIsLostDuringThePurgeAndRebuild_JournalsADedicatedAlarm()
    {
        // security-audit point 7, recommandation 2 : un kind de journal DISTINCT (ici "OwnerLost", nommé ainsi par
        // le teamleader) doit permettre à un administrateur de détecter l'incident SANS attendre un signalement
        // utilisateur — un "not-found" générique (déjà renvoyé aujourd'hui par la relecture de Success(), voir
        // assertion ci-dessous) est invisible dans le flot normal d'erreurs 404 attendues (IDOR, playlist supprimée...).
        var r = new Rig();
        r.Shares.Add("p1", Owner, "À voir", 3, (MemberWrite, "Write"));
        r.Shares.SimulateOwnerRowLostAfterDelete = true;

        var result = r.Service.RemoveMember(Owner, "p1", MemberWrite);

        Assert.False(result.Ok); // déjà vrai aujourd'hui (Success() relit via GetOwned -> null -> not-found)
        Assert.Equal(UserPageErrors.NotFound, result.Error);
        Assert.Contains(r.Journal.Of("OwnerLost"), e => e.PlaylistId == "p1" && e.UserId == Owner);
    }

    [Fact]
    public void AddOrUpdateMember_TheSamePurgeAndRebuildRiskDoesNotApply_UpsertShareNeverPurges()
    {
        // Contre-épreuve documentant POURQUOI seul RemoveMember est concerné : UpsertShare (contrat IShareGateway,
        // confirmé par EmbyShareGateway.cs:90 "une seule ligne suffit... aucune lecture préalable nécessaire") ne
        // purge JAMAIS les lignes existantes — la playlist ne peut donc jamais perdre son propriétaire à l'ajout
        // ou au changement de niveau d'un membre, contrairement au retrait (purge totale + reconstruction).
        var r = new Rig();
        r.Shares.Add("p1", Owner, "À voir", 3);

        r.Service.AddOrUpdateMember(Owner, "p1", MemberWrite, "Write");

        Assert.Empty(r.Journal.Of("OwnerLost"));
    }
}
