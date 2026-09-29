using EmbySharedPlaylist.UserPage;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// Tests de SPÉCIFICATION du QUOTA de playlists possédées à la création (v1.2.0, audit sécurité M1, décision utilisateur ;
/// docs/chronogrammes.md D22 et S11 lignes 7-10 ; contracts/http-endpoints.md « Quota » et « Ordre des vérifications ») :
/// refus 409 <c>limit-reached</c> si le demandeur possède déjà <c>UserPlaylistService.MaxOwnedPlaylists</c> (= 10) playlists ou plus ;
/// décompte de TOUTES les playlists possédées (partagées ou non, créées hors de la page comprises), jamais celles des autres comptes ;
/// ordre 403 -> 400 -> busy -> quota -> name-exists ; UNE seule lecture <c>ListOwnedPlaylists</c> ; rien n'est supprimé ni modifié ;
/// journal sans nom. La constante et le code d'erreur sont lus par réflexion : le fichier compile avant le code du lot (rouge attendu).
/// Complète UserPlaylistServiceCreateSpecTests (lot de moins de 50 cas, COMMON §15).
/// </summary>
public class UserPlaylistServiceQuotaSpecTests : UserPlaylistServiceSpecBase
{
    private const string LimitReached = "limit-reached";

    private static void OwnMany(Rig r, string owner, int n, string prefix = "own")
    {
        for (var i = 0; i < n; i++) r.Shares.Add($"{prefix}-{owner}-{i}", owner, $"Liste {prefix} {i}", 1);
    }

    [Fact]
    public void TheQuotaIsTheNamedServerConstant_10_AndTheErrorCodeIsLimitReached()
    {
        var field = typeof(UserPlaylistService).GetField("MaxOwnedPlaylists");
        Assert.NotNull(field);
        Assert.Equal(10, (int)field!.GetRawConstantValue()!);
        var code = typeof(UserPageErrors).GetField("LimitReached");
        Assert.NotNull(code);
        Assert.Equal(LimitReached, (string)code!.GetRawConstantValue()!);
    }

    [Fact]
    public void With9OwnedPlaylists_TheCreationSucceeds()
    {
        var r = new Rig();
        OwnMany(r, Owner, 9);
        Assert.True(r.Service.CreatePlaylist(Owner, "La dixième").Ok);
    }

    [Fact]
    public void With10OwnedPlaylists_TheCreationIsRefused_LimitReached_NothingCreated()
    {
        var r = new Rig();
        OwnMany(r, Owner, 10);
        var result = r.Service.CreatePlaylist(Owner, "La onzième");
        Assert.Equal(LimitReached, result.Error);
        Assert.Equal(0, r.Playlists.CreateCalls);
        Assert.Empty(r.Journal.Of("PlaylistCreated"));
    }

    [Fact]
    public void WithMoreThan10OwnedPlaylists_TheCreationIsRefused_AndNothingIsDeletedNorModified_S11Line8()
    {
        var r = new Rig();
        OwnMany(r, Owner, 12);
        var tagsBefore = r.Playlists.Playlists.ToDictionary(p => p.Key, p => p.Value.Tags.ToList());
        var result = r.Service.CreatePlaylist(Owner, "Encore une");
        Assert.Equal(LimitReached, result.Error);
        Assert.Equal(12, r.Playlists.Playlists.Count);                       // aucune création, aucune suppression
        Assert.Equal(0, r.Shares.DeleteCalls);
        Assert.Equal(0, r.Shares.UpsertCalls);
        foreach (var kv in r.Playlists.Playlists) Assert.Equal(tagsBefore[kv.Key], kv.Value.Tags);
        Assert.Equal(12, r.Service.ListOwned(Owner).Value!.Count);           // toujours gérées et listées normalement
    }

    [Fact]
    public void TheQuotaCountsAllOwnedPlaylists_SharedUnsharedManagedAndCreatedOutsideThePage_S11Line7()
    {
        var r = new Rig();
        for (var i = 0; i < 4; i++) r.Shares.Add($"u-{i}", Owner, $"Non partagée {i}", 0);                           // non gérées
        for (var i = 0; i < 3; i++) { r.Shares.Add($"s-{i}", Owner, $"Partagée {i}", 2, (MemberWrite, "Write")); r.Playlists.Playlists[$"s-{i}"].Tags.Add("remove-si-lu=NON"); }   // gérées
        for (var i = 0; i < 3; i++) r.Shares.Add($"n-{i}", Owner, $"Native {i}", 5);                                 // créées nativement, jamais par la page
        Assert.Equal(LimitReached, r.Service.CreatePlaylist(Owner, "Nouvelle").Error);
    }

    [Fact]
    public void TheQuotaIgnoresOtherAccountsPlaylists()
    {
        var r = new Rig();
        OwnMany(r, Owner, 9);
        OwnMany(r, Stranger, 25);
        Assert.True(r.Service.CreatePlaylist(Owner, "Ok").Ok);
    }

    [Fact]
    public void PlaylistsSharedWithTheRequesterButOwnedByAnother_DoNotCount()
    {
        var r = new Rig();
        OwnMany(r, Owner, 9);
        for (var i = 0; i < 5; i++) r.Shares.Add($"sh-{i}", Stranger, $"Chez Dan {i}", 1, (Owner, "Write"));
        Assert.True(r.Service.CreatePlaylist(Owner, "Ok").Ok);
    }

    [Fact]
    public void TheQuotaIsPerRequester_OneAccountAtTheLimitDoesNotBlockAnother()
    {
        var r = new Rig();
        OwnMany(r, Stranger, 10);
        Assert.Equal(LimitReached, r.Service.CreatePlaylist(Stranger, "Refusée").Error);
        Assert.True(r.Service.CreatePlaylist(Owner, "Acceptée").Ok);
    }

    [Fact]
    public void FromZero_Ten_CreationsSucceed_TheEleventhIsRefused()
    {
        var r = new Rig();
        for (var i = 0; i < 10; i++) Assert.True(r.Service.CreatePlaylist(Owner, $"Liste {i}").Ok, $"création {i}");
        Assert.Equal(LimitReached, r.Service.CreatePlaylist(Owner, "Liste 10").Error);
        Assert.Equal(10, r.Playlists.CreateCalls);
    }

    // ------------------------------------------------------------------ ordre des vérifications

    [Fact]
    public void TheQuotaIsCheckedBeforeUniqueness_10Owned_AndTheNameAlreadyTaken_IsLimitReached_S11Line9()
    {
        var r = new Rig();
        OwnMany(r, Owner, 9);
        r.Shares.Add("films", Owner, "Films", 1);                            // 10 possédées, dont « Films »
        Assert.Equal(LimitReached, r.Service.CreatePlaylist(Owner, " films ").Error);
    }

    [Fact]
    public void UnderTheQuota_UniquenessStillApplies()
    {
        var r = new Rig();
        OwnMany(r, Owner, 8);
        r.Shares.Add("films", Owner, "Films", 1);                            // 9 possédées
        Assert.Equal(UserPageErrors.NameExists, r.Service.CreatePlaylist(Owner, "FILMS").Error);
    }

    [Fact]
    public void SharingDisabled_403_IsEvaluatedBeforeTheQuota()
    {
        var r = new Rig();
        r.Users.Add("nope", "NoPerm", canShare: false);
        OwnMany(r, "nope", 12);
        Assert.Equal(UserPageErrors.SharingDisabled, r.Service.CreatePlaylist("nope", "Films").Error);
    }

    [Fact]
    public void InvalidName_400_IsEvaluatedBeforeTheQuota()
    {
        var r = new Rig();
        OwnMany(r, Owner, 10);
        Assert.Equal(UserPageErrors.InvalidName, r.Service.CreatePlaylist(Owner, "").Error);
        Assert.Equal(UserPageErrors.InvalidName, r.Service.CreatePlaylist(Owner, new string('x', 101)).Error);
    }

    // ------------------------------------------------------------------ lecture unique, journal, concurrence

    [Fact]
    public void QuotaAndUniqueness_ShareASingleListOwnedPlaylistsRead()
    {
        var r = new Rig();
        OwnMany(r, Owner, 3);
        r.Service.CreatePlaylist(Owner, "Nouvelle");
        Assert.Equal(1, r.Shares.ListOwnedOwners.Count(o => o == Owner));
    }

    [Fact]
    public void ARefusalByTheQuota_ReadsTheListOnce_AndWritesNoJournalEntryWithTheName()
    {
        var r = new Rig();
        OwnMany(r, Owner, 10);
        r.Service.CreatePlaylist(Owner, "Nom confidentiel du refus");
        Assert.Equal(1, r.Shares.ListOwnedOwners.Count(o => o == Owner));
        Assert.DoesNotContain("confidentiel", string.Join(" ", r.Journal.Entries.Select(e => e.Detail + e.PlaylistId + e.UserId)));
        Assert.Empty(r.Journal.Of("PlaylistCreated"));
    }

    [Fact]
    public void TwoSimultaneousCreationsAt9Owned_OnlyOneSucceeds_TheOtherIsLimitReached_TheQuotaCannotBeRaced()
    {
        for (var i = 0; i < 8; i++)
        {
            var r = new Rig();
            OwnMany(r, Owner, 9);
            r.Playlists.OnCreate = () => Thread.Sleep(30);
            using var start = new ManualResetEventSlim();
            var tasks = new[] { "A", "B" }.Select(n => Task.Run(() => { start.Wait(); return r.Service.CreatePlaylist(Owner, n); })).ToArray();
            start.Set();
            Task.WaitAll(tasks);
            Assert.Equal(1, tasks.Count(t => t.Result.Ok));
            Assert.Equal(new[] { LimitReached }, tasks.Where(t => !t.Result.Ok).Select(t => t.Result.Error));
            Assert.Equal(1, r.Playlists.CreateCalls);
        }
    }
}
