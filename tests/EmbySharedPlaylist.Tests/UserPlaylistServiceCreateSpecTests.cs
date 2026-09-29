using System.Reflection;
using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Marker;
using EmbySharedPlaylist.UserPage;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// Tests de SPÉCIFICATION de la création de playlist depuis la page (v1.2.0, #55, D22 ; docs/chronogrammes.md S11 ;
/// contracts/http-endpoints.md « POST /SharedPlaylist/User/Playlists » ; plan `_work/reports/planner-20260929-123201.md` §2 lot C,
/// tâches C1-C3). Écrits AVANT le code : ROUGES tant que le lot C n'est pas implémenté (attendu).
/// Contrat proposé (à confirmer par dev-plugin, à signaler au teamleader en cas d'écart) :
/// <list type="bullet">
/// <item><c>IPlaylistGateway.CreatePlaylist(string ownerId, string name) -&gt; string</c> (id de la playlist créée, vide, Vidéo) — faux dans
/// <see cref="UserPlaylistServiceSpecBase"/>.</item>
/// <item><c>UserPlaylistService.CreatePlaylist(string requesterId, string name) -&gt; UserPageResult&lt;UserPagePlaylistDto&gt;</c> : le propriétaire
/// est l'identité de session, JAMAIS un paramètre ; <c>CanShare</c> en premier (403) ; nom : trim (<c>char.IsWhiteSpace</c>), NFC, 1 à 100
/// caractères APRÈS normalisation, aucun caractère de contrôle (400 <c>invalid-name</c>) ; verrou de création par propriétaire (409
/// <c>busy</c>) sous lequel : unicité par propriétaire (<c>OrdinalIgnoreCase</c> après la même normalisation, accents et espaces internes
/// significatifs ; 409 <c>name-exists</c>), création, relecture <c>GetOwned</c> ; journal <c>PlaylistCreated</c> (ids seulement, jamais le nom) ;
/// tout sous <c>Guard</c> (500 <c>internal</c>).</item>
/// <item><c>UserPageErrors.InvalidName = "invalid-name"</c>, <c>UserPageErrors.NameExists = "name-exists"</c>.</item>
/// </list>
/// La playlist créée est NON partagée donc NON gérée : ni étiquette ni message d'aide (D6) tant qu'aucun membre n'est ajouté (S10).
/// </summary>
public class UserPlaylistServiceCreateSpecTests : UserPlaylistServiceSpecBase
{
    private static string Long(int n) => new('x', n);

    // ------------------------------------------------------------------ constantes et signature

    [Fact]
    public void ErrorCodes_AreTheContractOnes()
    {
        Assert.Equal("invalid-name", UserPageErrors.InvalidName);
        Assert.Equal("name-exists", UserPageErrors.NameExists);
    }

    [Fact]
    public void CreatePlaylist_TakesNoOwnerParameter_TheOwnerIsTheSessionIdentity_AntiIdor()
    {
        var method = typeof(UserPlaylistService).GetMethod("CreatePlaylist");
        Assert.NotNull(method);
        var parameters = method!.GetParameters();
        Assert.Equal(2, parameters.Length);                       // identité de session + nom : jamais un identifiant de propriétaire
        Assert.Equal("requesterId", parameters[0].Name);
        Assert.Equal(typeof(string), parameters[1].ParameterType);
    }

    // ------------------------------------------------------------------ autorisation

    [Fact]
    public void WithoutPermission_ReturnsSharingDisabled_NothingCreated()
    {
        var r = new Rig();
        r.Users.Add("nope", "NoPerm", canShare: false);
        var result = r.Service.CreatePlaylist("nope", "Films");
        Assert.Equal(UserPageErrors.SharingDisabled, result.Error);
        Assert.Equal(0, r.Playlists.CreateCalls);
    }

    [Fact]
    public void CanShare_IsEvaluatedBeforeTheNameValidation()
    {
        var r = new Rig();
        r.Users.Add("nope", "NoPerm", canShare: false);
        Assert.Equal(UserPageErrors.SharingDisabled, r.Service.CreatePlaylist("nope", "").Error);       // nom invalide ET pas de permission
        Assert.Equal(UserPageErrors.SharingDisabled, r.Service.CreatePlaylist("nope", null!).Error);
    }

    [Fact]
    public void UnknownRequester_IsRefusedLikeAMissingPermission()
    {
        var r = new Rig();
        Assert.Equal(UserPageErrors.SharingDisabled, r.Service.CreatePlaylist("ghost", "Films").Error);
        Assert.Equal(0, r.Playlists.CreateCalls);
    }

    // ------------------------------------------------------------------ validation du nom

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    [InlineData("  　")]          // espaces Unicode uniquement
    [InlineData(null)]
    [InlineData("a\u0000b")]                    // caractère de contrôle Cc
    [InlineData("a\nb")]
    [InlineData("a\tb")]
    [InlineData("\u007F")]
    [InlineData("Films\u0085du dimanche")]      // NEL (Cc)
    public void InvalidNames_Are400InvalidName_NothingCreated(string? name)
    {
        var r = new Rig();
        var result = r.Service.CreatePlaylist(Owner, name!);
        Assert.False(result.Ok);
        Assert.Equal(UserPageErrors.InvalidName, result.Error);
        Assert.Equal(0, r.Playlists.CreateCalls);
        Assert.Empty(r.Journal.Of("PlaylistCreated"));
    }

    [Fact]
    public void ANameOf101Characters_IsInvalid_100IsValid_1IsValid()
    {
        var r = new Rig();
        Assert.Equal(UserPageErrors.InvalidName, r.Service.CreatePlaylist(Owner, Long(101)).Error);
        Assert.True(r.Service.CreatePlaylist(Owner, Long(100)).Ok);
        Assert.True(r.Service.CreatePlaylist(Owner, "y").Ok);
    }

    [Fact]
    public void TheLengthIsMeasuredAfterTrim_100PlusBorderSpacesIsValid_101AfterTrimIsNot()
    {
        var r = new Rig();
        Assert.True(r.Service.CreatePlaylist(Owner, "  " + Long(100) + "  ").Ok);
        Assert.Equal(UserPageErrors.InvalidName, r.Service.CreatePlaylist(Owner, "  " + Long(101) + "  ").Error);
    }

    [Fact]
    public void TheLengthIsMeasuredAfterNfc_DecomposedSequencesThatComposeToFewerCharactersAreValid()
    {
        var r = new Rig();
        var decomposed = string.Concat(Enumerable.Repeat("é", 51));      // 102 caractères avant NFC, 51 après
        var result = r.Service.CreatePlaylist(Owner, decomposed);
        Assert.True(result.Ok);
        Assert.Equal(string.Concat(Enumerable.Repeat("é", 51)), result.Value!.Name);
    }

    // ------------------------------------------------------------------ succès : forme et normalisation

    [Fact]
    public void Success_ReturnsAnEmptyUnsharedPlaylist_WithThreeNoneOptions_S11Step1()
    {
        var r = new Rig();
        var result = r.Service.CreatePlaylist(Owner, "Films du dimanche");

        Assert.True(result.Ok);
        var dto = result.Value!;
        Assert.False(string.IsNullOrEmpty(dto.PlaylistId));
        Assert.Equal("Films du dimanche", dto.Name);
        Assert.False(dto.IsShared);
        Assert.Empty(dto.Members);
        Assert.Equal(0, dto.ItemCount);
        Assert.Equal(new[] { "propager-avancement", "propager-lu", "remove-si-lu" }, dto.Options.Keys.OrderBy(k => k));
        Assert.All(dto.Options.Values, v => Assert.Equal("None", v));
    }

    [Fact]
    public void TheOwnerPassedToThePortIsTheRequester_AndTheStoredNameIsTheNormalisedOne()
    {
        var r = new Rig();
        r.Service.CreatePlaylist(Owner, "   Été  ");
        var created = Assert.Single(r.Playlists.CreateLog);
        Assert.Equal(Owner, created.Owner);
        Assert.Equal("Été", created.Name);
    }

    [Fact]
    public void TheStoredNameIsNfc_EvenWhenGivenDecomposed()
    {
        var r = new Rig();
        r.Service.CreatePlaylist(Owner, "Été");
        Assert.Equal("Été", Assert.Single(r.Playlists.CreateLog).Name);
    }

    [Fact]
    public void InnerSpacesAreKept()
    {
        var r = new Rig();
        r.Service.CreatePlaylist(Owner, "À  voir");
        Assert.Equal("À  voir", Assert.Single(r.Playlists.CreateLog).Name);
    }

    [Fact]
    public void TheCreatedPlaylist_IsUnmanaged_NoTagNoHelpMessageNoFirstDetection_D6()
    {
        var r = new Rig();
        var id = r.Service.CreatePlaylist(Owner, "Films").Value!.PlaylistId;
        Assert.Empty(r.Playlists.Playlists[id].Tags);
        Assert.Null(r.Playlists.Playlists[id].Overview);
        Assert.False(r.Seen.IsSeen(id));
        Assert.Empty(r.Journal.Of("MarkerPosed"));
        Assert.Empty(r.Journal.Of("DescriptionWritten"));
        Assert.Equal(0, r.Shares.UpsertCalls);
    }

    [Fact]
    public void TheCreatedPlaylist_AppearsInListOwned_Unshared()
    {
        var r = new Rig();
        var id = r.Service.CreatePlaylist(Owner, "Films").Value!.PlaylistId;
        var dto = Assert.Single(r.Service.ListOwned(Owner).Value!, p => p.PlaylistId == id);
        Assert.False(dto.IsShared);
        Assert.Equal("Films", dto.Name);
    }

    [Fact]
    public void S11ThenS10_AddingTheFirstMemberToACreatedPlaylist_TriggersTheFirstDetection_ThreeNon()
    {
        var r = new Rig();
        var id = r.Service.CreatePlaylist(Owner, "Films du dimanche").Value!.PlaylistId;

        var shared = r.Service.AddOrUpdateMember(Owner, id, MemberRead, "Read");

        Assert.True(shared.Ok);
        Assert.True(shared.Value!.IsShared);
        var tags = r.Playlists.Playlists[id].Tags;
        Assert.Contains("remove-si-lu=NON", tags);
        Assert.Contains("propager-lu=NON", tags);
        Assert.Contains("propager-avancement=NON", tags);
        Assert.NotNull(r.Playlists.Playlists[id].Overview);
    }

    // ------------------------------------------------------------------ unicité par propriétaire

    [Fact]
    public void AnExactDuplicate_Is409NameExists_NoSecondCreation()
    {
        var r = new Rig();
        Assert.True(r.Service.CreatePlaylist(Owner, "Films du dimanche").Ok);
        var again = r.Service.CreatePlaylist(Owner, "Films du dimanche");
        Assert.Equal(UserPageErrors.NameExists, again.Error);
        Assert.Equal(1, r.Playlists.CreateCalls);
    }

    [Theory]
    [InlineData("  films du DIMANCHE ")]                       // S11 ligne 2 : trim + casse
    [InlineData("FILMS DU DIMANCHE")]
    [InlineData(" Films du dimanche ")]              // espaces Unicode de bord
    public void DuplicatesAfterNormalisation_AreNameExists(string second)
    {
        var r = new Rig();
        r.Service.CreatePlaylist(Owner, "Films du dimanche");
        Assert.Equal(UserPageErrors.NameExists, r.Service.CreatePlaylist(Owner, second).Error);
    }

    [Fact]
    public void ANfcVariantOfAnExistingName_IsNameExists()
    {
        var r = new Rig();
        r.Service.CreatePlaylist(Owner, "Été");                // précomposé
        Assert.Equal(UserPageErrors.NameExists, r.Service.CreatePlaylist(Owner, "Été").Error);
    }

    [Fact]
    public void ComparisonIsAgainstPlaylistsCreatedOutsideThePageToo_TheOwnedListIsTheReference()
    {
        var r = new Rig();
        r.Shares.Add("native1", Owner, "À voir", 3);            // créée nativement dans Emby, même périmètre que GET User/Playlists
        Assert.Equal(UserPageErrors.NameExists, r.Service.CreatePlaylist(Owner, " à VOIR ").Error);
    }

    [Fact]
    public void AccentsAreSignificant_EteAndEteAreTwoNames()
    {
        var r = new Rig();
        Assert.True(r.Service.CreatePlaylist(Owner, "Été").Ok);
        Assert.True(r.Service.CreatePlaylist(Owner, "Ete").Ok);
    }

    [Fact]
    public void InnerSpacesAreSignificant_TwoSpacesAndOneSpaceAreTwoNames()
    {
        var r = new Rig();
        Assert.True(r.Service.CreatePlaylist(Owner, "À  voir").Ok);
        Assert.True(r.Service.CreatePlaylist(Owner, "À voir").Ok);
    }

    [Fact]
    public void OrdinalIgnoreCase_DoesNotFoldEszettToSs()
    {
        var r = new Rig();
        Assert.True(r.Service.CreatePlaylist(Owner, "Straße").Ok);
        Assert.True(r.Service.CreatePlaylist(Owner, "STRASSE").Ok);
    }

    [Fact]
    public void UniquenessIsPerOwner_AnotherAccountMayHaveTheSameName_S11Step4()
    {
        var r = new Rig();
        r.Shares.Add("p-dan", Stranger, "À voir", 3);           // U4 possède déjà « À voir »
        var result = r.Service.CreatePlaylist(Owner, "À voir");
        Assert.True(result.Ok);
    }

    [Fact]
    public void OtherAccountsPlaylistsAreNeverConsulted_OnlyTheRequesterOwnedListIsRead()
    {
        var r = new Rig();
        r.Shares.Add("p-dan", Stranger, "Films", 3);
        r.Service.CreatePlaylist(Owner, "Films");
        Assert.All(r.Shares.ListOwnedOwners, o => Assert.Equal(Owner, o));
    }

    [Fact]
    public void APlaylistSharedWithTheRequesterButOwnedByAnother_DoesNotBlockTheName()
    {
        var r = new Rig();
        r.Shares.Add("p-dan", Stranger, "Films", 3, (Owner, "Write"));
        Assert.True(r.Service.CreatePlaylist(Owner, "Films").Ok);
    }

    // ------------------------------------------------------------------ journal

    [Fact]
    public void PlaylistCreated_IsJournaled_IdsOnly_NeverTheName()
    {
        var r = new Rig();
        var id = r.Service.CreatePlaylist(Owner, "Mon nom secret de playlist").Value!.PlaylistId;
        var entry = Assert.Single(r.Journal.Of("PlaylistCreated"));
        Assert.Equal(id, entry.PlaylistId);
        Assert.Equal(Owner, entry.UserId);
        Assert.True(string.IsNullOrEmpty(entry.Detail));
        Assert.DoesNotContain("secret", string.Join(" ", r.Journal.Entries.Select(e => e.Detail + e.PlaylistId + e.UserId)));
    }

    [Fact]
    public void NoJournalEntryIsWrittenOnRefusals()
    {
        var r = new Rig();
        r.Service.CreatePlaylist(Owner, "Films");
        r.Service.CreatePlaylist(Owner, "films");     // name-exists
        r.Service.CreatePlaylist(Owner, "");          // invalid-name
        Assert.Single(r.Journal.Of("PlaylistCreated"));
    }

    // ------------------------------------------------------------------ « ne lève jamais »

    [Fact]
    public void AGatewayFailure_IsNeverThrown_JournaledAsErrorByTypeOnly_ReturnsInternal()
    {
        var r = new Rig();
        r.Playlists.ThrowOnCreate = true;
        UserPageResult<UserPagePlaylistDto>? result = null;
        var ex = Record.Exception(() => result = r.Service.CreatePlaylist(Owner, "Films"));
        Assert.Null(ex);
        Assert.Equal(UserPageErrors.Internal, result!.Error);
        Assert.NotEmpty(r.Journal.Of("Error"));
        Assert.DoesNotContain("secret", string.Join(" ", r.Journal.Entries.Select(e => e.Detail)));
        Assert.Empty(r.Journal.Of("PlaylistCreated"));
    }

    [Fact]
    public void ACreatedPlaylistNotReadBackAsOwned_Is500Internal_WithAnErrorEntry_NoPlaylistCreatedJournal()
    {
        var r = new Rig();
        r.Shares.HideCreatedFromGetOwned = true;
        var result = r.Service.CreatePlaylist(Owner, "Films");
        Assert.Equal(UserPageErrors.Internal, result.Error);
        Assert.Contains(r.Journal.Of("Error"), e => (e.Detail ?? "").Contains("created-not-owned"));
        Assert.DoesNotContain("Films", string.Join(" ", r.Journal.Entries.Select(e => e.Detail)));
    }

    // ------------------------------------------------------------------ noms hostiles (F1/m2 de la revue et de l'audit)

    [Theory]
    [InlineData("\u200B")]                     // espace de largeur nulle seul (Cf)
    [InlineData("\u200B\u200B  \u200B")]        // uniquement des Cf et des espaces
    [InlineData("a\u200Bb")]                   // Cf au milieu
    [InlineData("Films\u202Etxt.exe")]         // U+202E RIGHT-TO-LEFT OVERRIDE (Cf) : usurpation d'affichage
    [InlineData("a\u200Db")]                   // ZWJ (Cf)
    [InlineData("\uFEFFFilms")]                // BOM (Cf, non blanc)
    [InlineData("a\u2028b")]                   // séparateur de ligne (Zl)
    [InlineData("a\u2029b")]                   // séparateur de paragraphe (Zp)
    [InlineData("a\u0378b")]                   // point de code non assigné
    [InlineData("a\u0000b")]                   // NUL
    [InlineData("a\nb")]
    public void HostileNames_AreRefused_InvalidName_NothingCreated(string name)
    {
        var r = new Rig();
        var result = r.Service.CreatePlaylist(Owner, name);
        Assert.Equal(UserPageErrors.InvalidName, result.Error);
        Assert.Equal(0, r.Playlists.CreateCalls);
    }

    [Theory]
    [InlineData(0xD800)]                        // surrogate haut isolé
    [InlineData(0xDC00)]                        // surrogate bas isolé
    public void LoneSurrogates_AreRefused_BuiltAtRuntime_AttributeDataWouldBeCorrupted(int codeUnit)
    {
        // Construit à l'exécution : un surrogate isolé dans un attribut est remplacé par U+FFFD à la compilation.
        var r = new Rig();
        var name = "a" + (char)codeUnit + "b";
        Assert.Equal(UserPageErrors.InvalidName, r.Service.CreatePlaylist(Owner, name).Error);
        Assert.Equal(0, r.Playlists.CreateCalls);
    }

    [Fact]
    public void AHostile101CharactersName_IsRefused()
    {
        var r = new Rig();
        Assert.Equal(UserPageErrors.InvalidName, r.Service.CreatePlaylist(Owner, new string('z', 101)).Error);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("Films 🎬 du soir")]            // paire de surrogates valide (emoji) : accepté
    public void NamesWithPathOrMarkupCharacters_AreAccepted_VerbatimAsData_TheNameIsNeverInterpreted(string name)
    {
        // Documenté : le plugin ne refuse QUE les contrôles, Cf, séparateurs de ligne/paragraphe, non assignés et surrogates isolés.
        // Un nom n'est jamais un chemin ni du HTML côté plugin (l'affichage de la page n'utilise jamais innerHTML).
        var r = new Rig();
        var result = r.Service.CreatePlaylist(Owner, name);
        Assert.True(result.Ok, result.Error);
        Assert.Equal(name, Assert.Single(r.Playlists.CreateLog).Name);
    }

    // ------------------------------------------------------------------ concurrence et verrou de création

    [Fact]
    public void TwoSimultaneousCreationsOfTheSameName_OnlyOneSucceeds_TheOtherIsNameExists()
    {
        for (var i = 0; i < 10; i++)
        {
            var r = new Rig();
            r.Playlists.OnCreate = () => Thread.Sleep(30);        // élargit la fenêtre de course
            using var start = new ManualResetEventSlim();
            var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(() => { start.Wait(); return r.Service.CreatePlaylist(Owner, "Films"); })).ToArray();
            start.Set();
            Task.WaitAll(tasks);

            Assert.Equal(1, tasks.Count(t => t.Result.Ok));
            Assert.Equal(new[] { UserPageErrors.NameExists }, tasks.Where(t => !t.Result.Ok).Select(t => t.Result.Error));
            Assert.Equal(1, r.Playlists.CreateCalls);
            Assert.Single(r.Journal.Of("PlaylistCreated"));
        }
    }

    [Fact]
    public void TwoSimultaneousCreationsOfDifferentNames_BothSucceed()
    {
        var r = new Rig();
        r.Playlists.OnCreate = () => Thread.Sleep(20);
        var tasks = new[] { "A", "B" }.Select(n => Task.Run(() => r.Service.CreatePlaylist(Owner, n))).ToArray();
        Task.WaitAll(tasks);
        Assert.All(tasks, t => Assert.True(t.Result.Ok, t.Result.Error));
        Assert.Equal(2, r.Playlists.CreateCalls);
    }

    [Fact]
    public void TheCreationLockIsPerOwner_TwoOwnersCreatingTheSameNameSimultaneouslyBothSucceed()
    {
        var r = new Rig();
        r.Playlists.OnCreate = () => Thread.Sleep(30);
        var tasks = new[] { Owner, Stranger }.Select(o => Task.Run(() => r.Service.CreatePlaylist(o, "À voir"))).ToArray();
        Task.WaitAll(tasks);
        Assert.All(tasks, t => Assert.True(t.Result.Ok, t.Result.Error));
    }

    [Fact]
    public void WhileACreationOfTheSameOwnerIsInProgress_AnotherIsBusy_ThenTheFirstCompletes()
    {
        var r = new Rig();                                             // délai de verrou du Rig : 500 ms
        using var inside = new ManualResetEventSlim();
        r.Playlists.OnCreate = () => { inside.Set(); Thread.Sleep(1500); };
        var first = Task.Run(() => r.Service.CreatePlaylist(Owner, "Premiere"));
        Assert.True(inside.Wait(TimeSpan.FromSeconds(5)));
        r.Playlists.OnCreate = null;

        var second = r.Service.CreatePlaylist(Owner, "Seconde");

        Assert.Equal(UserPageErrors.Busy, second.Error);
        Assert.True(first.Result.Ok);
        Assert.Equal(1, r.Playlists.CreateCalls);
    }

    [Fact]
    public void TheCreationLock_DoesNotBlockAnotherOwnersMemberOperations_NorTheReverse()
    {
        var r = new Rig();
        r.Shares.Add("p1", Stranger, "À voir", 3);
        using var inside = new ManualResetEventSlim();
        r.Playlists.OnCreate = () => { inside.Set(); Thread.Sleep(800); };
        var creation = Task.Run(() => r.Service.CreatePlaylist(Owner, "Films"));
        Assert.True(inside.Wait(TimeSpan.FromSeconds(5)));

        var other = r.Service.AddOrUpdateMember(Stranger, "p1", MemberWrite, "Write");   // autre propriétaire, autre verrou

        Assert.True(other.Ok, other.Error);
        Assert.True(creation.Result.Ok);
    }
}
