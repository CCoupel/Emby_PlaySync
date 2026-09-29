using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Emby;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// Tests de SPÉCIFICATION des nouveaux kinds de journal de la page utilisateur (v1.1.0, #39,
/// <c>contracts/http-endpoints.md</c> « Journal (Diagnostics/Journal) — nouveaux kinds (v1.1.0) ») : <c>ShareChanged</c>
/// et <c>MarkerSet</c> font partie des « décisions du moteur » renvoyées sans filtre <c>kind</c> (comme <c>Removal</c>,
/// <c>PermissionPosed</c>…, cf. <see cref="DiagnosticsMapper.EngineKinds"/>). Complète <c>DiagnosticsMapperDevTests</c>
/// (existant, immuable) sans le dupliquer : ce fichier ne teste QUE l'ajout v1.1.0.
/// </summary>
public class UserPageDiagnosticsSpecTests
{
    private static JournalEntry E(string kind, string? playlist = null, string? user = null, string? detail = null) =>
        new() { Ts = "2026-09-28T12:00:00.0000000Z", Kind = kind, PlaylistId = playlist, UserId = user, Detail = detail };

    [Fact]
    public void EngineKinds_IncludesShareChangedAndMarkerSet()
    {
        Assert.Contains("ShareChanged", DiagnosticsMapper.EngineKinds);
        Assert.Contains("MarkerSet", DiagnosticsMapper.EngineKinds);
    }

    [Fact]
    public void Journal_WithoutFilter_ReturnsShareChangedAndMarkerSet_AmongEngineDecisions()
    {
        var entries = new[]
        {
            E("ScanPass"), E("ShareChanged", "p1", "u2", "action=add level=Write"),
            E("MarkerSet", "p1", "u1", "family=remove-si-lu value=OUI removed=1"), E("Probe")
        };
        var kinds = DiagnosticsMapper.Journal(entries, null).Select(e => e.Kind).ToList();
        Assert.Contains("ShareChanged", kinds);
        Assert.Contains("MarkerSet", kinds);
        Assert.DoesNotContain("Probe", kinds);
    }

    [Fact]
    public void Journal_FilteredByKind_ReturnsExactlyShareChanged_CaseInsensitive()
    {
        var entries = new[] { E("ShareChanged", "p1", "u2"), E("MarkerSet", "p1", "u1"), E("Removal", "p1") };
        var dto = DiagnosticsMapper.Journal(entries, "sharechanged");
        var kind = Assert.Single(dto);
        Assert.Equal("ShareChanged", kind.Kind);
    }

    [Fact]
    public void ShareChanged_Entry_CarriesPlaylistAndMemberIds_DetailIsActionAndLevelOnly()
    {
        // Contrat : UserId = membre concerné (pas le propriétaire, qui est le demandeur implicite), ids seulement.
        var entries = new[] { E("ShareChanged", "p1", "u2", "action=add level=Write") };
        var dto = Assert.Single(DiagnosticsMapper.Journal(entries, "ShareChanged"));
        Assert.Equal("p1", dto.PlaylistId);
        Assert.Equal("u2", dto.UserId);
        Assert.Equal("action=add level=Write", dto.Detail);
    }

    [Fact]
    public void MarkerSet_Entry_CarriesPlaylistAndOwnerId_DetailIsFamilyValueRemoved()
    {
        // Contrat : UserId = propriétaire (action explicite du propriétaire, D19).
        var entries = new[] { E("MarkerSet", "p1", "u1", "family=propager-lu value=NON removed=2") };
        var dto = Assert.Single(DiagnosticsMapper.Journal(entries, "MarkerSet"));
        Assert.Equal("p1", dto.PlaylistId);
        Assert.Equal("u1", dto.UserId);
        Assert.Equal("family=propager-lu value=NON removed=2", dto.Detail);
    }

    [Fact]
    public void EngineKinds_NeverContainsUserOrPlaylistNames_OnlyKnownKinds()
    {
        // Garde-fou de non-régression du fichier : la liste des kinds "moteur" reste une liste de NOMS DE KIND
        // (ids/énumérations), jamais un nom de compte ou de playlist (cf. contrat Diagnostics : "aucun nom").
        Assert.All(DiagnosticsMapper.EngineKinds, k => Assert.DoesNotContain(" ", k));
    }

    // ------------------------------------------------------------------ security-audit-20260928-154256.md point 7
    // (MOYENNE) : kind d'alarme dédié pour un retrait de membre qui aurait perdu la ligne ManageDelete du
    // propriétaire (purge+reconstruction non transactionnelle d'EmbyShareGateway.DeleteShare). ROUGE tant que
    // dev-plugin n'a pas ajouté "OwnerLost" à EngineKinds (voir aussi UserPlaylistServiceSpecTests.cs,
    // RemoveMember_WhenTheOwnersManageDeleteRowIsLostDuringThePurgeAndRebuild_JournalsADedicatedAlarm) — doit
    // rester visible SANS filtre kind (décisions du moteur), comme ShareChanged/MarkerSet.

    [Fact]
    public void EngineKinds_IncludesOwnerLost()
    {
        Assert.Contains("OwnerLost", DiagnosticsMapper.EngineKinds);
    }

    [Fact]
    public void Journal_WithoutFilter_ReturnsOwnerLost_AmongEngineDecisions()
    {
        var entries = new[] { E("ScanPass"), E("OwnerLost", "p1", "u1", "context=RemoveMember") };
        var kinds = DiagnosticsMapper.Journal(entries, null).Select(e => e.Kind).ToList();
        Assert.Contains("OwnerLost", kinds);
    }

    [Fact]
    public void MarkerSet_ForPropagerAvancement_IsExposedLikeTheOtherFamilies_v120()
    {
        // v1.2.0 (D21) : <Family> inclut propager-avancement dans le Detail de MarkerSet (ids seulement, aucun nom).
        var entries = new[] { E("MarkerSet", "p1", "u1", "family=propager-avancement value=OUI removed=1") };
        var dto = Assert.Single(DiagnosticsMapper.Journal(entries, null));
        Assert.Equal("MarkerSet", dto.Kind);
        Assert.Equal("family=propager-avancement value=OUI removed=1", dto.Detail);
    }
}
