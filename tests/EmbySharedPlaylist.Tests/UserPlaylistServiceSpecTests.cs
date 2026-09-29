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
public class UserPlaylistServiceSpecTests : UserPlaylistServiceSpecBase
{
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

}
