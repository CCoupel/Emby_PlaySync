using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Marker;
using EmbySharedPlaylist.Reconciliation;
using EmbySharedPlaylist.UserPage;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>Lot 2 de la spécification de <see cref="UserPlaylistService"/> : listes (forme), sécurité (audit 20260928-154256 point 7),
/// sérialisation d'Options, et trois familles v1.2.0 (#56, D21). Faux et Rig : <see cref="UserPlaylistServiceSpecBase"/>.</summary>
public class UserPlaylistServiceSecuritySpecTests : UserPlaylistServiceSpecBase
{
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

    // ------------------------------------------------------------------ Sécurité/QA — Options sérialisé en tableau
    // (qa-20260928-160840.md §1.1, CRITIQUE) : IReadOnlyDictionary<string,string> était émis par le sérialiseur
    // JSON de l'hôte Emby comme un TABLEAU de paires {Key,Value} au lieu de l'objet {"remove-si-lu":"Oui",...}
    // documenté par le contrat — cassait l'affichage des interrupteurs côté page (toujours décochés, confirmé en
    // QUALIF). Impossible de rejouer le sérialiseur RÉEL de l'hôte dans ce projet de tests (ServiceStack.Text
    // n'est pas référencé, seuls les DLL du SDK Emby le sont, aucun dotnet côté dev-plugin pour vérifier
    // autrement) : ce test structurel (réflexion sur le type DÉCLARÉ de la propriété) est le meilleur filet
    // disponible ici — il échoue si la propriété est un jour re-déclarée en interface (IReadOnlyDictionary/
    // IDictionary), la régression exacte qui a causé le bug.

    [Fact]
    public void UserPagePlaylistDto_Options_IsAConcreteDictionaryType_NeverAnInterface()
    {
        var property = typeof(UserPagePlaylistDto).GetProperty(nameof(UserPagePlaylistDto.Options));
        Assert.NotNull(property);
        Assert.Equal(typeof(Dictionary<string, string>), property!.PropertyType);
        Assert.False(property.PropertyType.IsInterface, "Options ne doit jamais être déclaré via une interface (IReadOnlyDictionary/IDictionary) : le sérialiseur JSON de l'hôte Emby les émet comme un tableau de paires {Key,Value}, pas un objet.");
    }

    // ------------------------------------------------------------------ v1.2.0 (#56, D21, tâche B8) : trois familles — ajout ADDITIF

    private static readonly string[] ThreeKeys = { "remove-si-lu", "propager-lu", "propager-avancement" };

    [Fact]
    public void Options_AlwaysExposeExactlyTheThreeFamilyKeys_EvenWithoutAnyTag_v120()
    {
        var r = new Rig();
        r.Shares.Add("p1", Owner, "À voir", 3);                                   // non partagée, aucune étiquette
        var dto = Assert.Single(r.Service.ListOwned(Owner).Value!, p => p.PlaylistId == "p1");
        Assert.Equal(ThreeKeys.OrderBy(k => k), dto.Options.Keys.OrderBy(k => k));
        Assert.All(dto.Options.Values, v => Assert.Equal("None", v));
    }

    [Fact]
    public void Options_ReflectTheStateOfEachFamilyIndependently_v120()
    {
        var r = new Rig();
        r.Shares.Add("p1", Owner, "À voir", 3, (MemberWrite, "Write"));
        r.Playlists.Playlists["p1"].Tags.AddRange(new[] { "remove-si-lu=OUI", "propager-lu=NON", "propager-avancement=OUI", "propager-avancement=NON" });
        var dto = Assert.Single(r.Service.ListOwned(Owner).Value!, p => p.PlaylistId == "p1");
        Assert.Equal("Oui", dto.Options["remove-si-lu"]);          // l'état, PAS l'effet : inactif sans propager-lu=Oui (D21), signalé par la page
        Assert.Equal("Non", dto.Options["propager-lu"]);
        Assert.Equal("Both", dto.Options["propager-avancement"]);
    }

    [Fact]
    public void FirstShare_PosesThePropagerAvancementNon_NoInheritance_v120()
    {
        var r = new Rig();
        r.Shares.Add("p1", Owner, "À voir", 3);
        r.Playlists.Playlists["p1"].Tags.Add("propager-lu=OUI");                   // ancien réglage qui couvrait l'avancement
        var result = r.Service.AddOrUpdateMember(Owner, "p1", MemberWrite, "Write");
        Assert.True(result.Ok);
        Assert.Equal("Non", result.Value!.Options["propager-avancement"]);
        Assert.Equal("Oui", result.Value.Options["propager-lu"]);
        Assert.Contains("propager-avancement=NON", r.Playlists.Playlists["p1"].Tags);
        Assert.DoesNotContain("propager-avancement=OUI", r.Playlists.Playlists["p1"].Tags);
    }

    [Theory]
    [InlineData(true, "OUI", "Oui")]
    [InlineData(false, "NON", "Non")]
    public void SetOption_PropagerAvancement_IsAccepted_ReplacesOnlyItsFamily_JournalsMarkerSet_v120(bool enabled, string journalValue, string optionValue)
    {
        var r = new Rig();
        r.Shares.Add("p1", Owner, "À voir", 3, (MemberWrite, "Write"));
        r.Playlists.Playlists["p1"].Tags.AddRange(new[] { "remove-si-lu=OUI", "propager-lu=OUI", "propager-avancement=" + (enabled ? "NON" : "OUI"), "favori" });

        var result = r.Service.SetOption(Owner, "p1", "propager-avancement", enabled);

        Assert.True(result.Ok);
        Assert.Equal(optionValue, result.Value!.Options["propager-avancement"]);
        var tags = r.Playlists.Playlists["p1"].Tags;
        Assert.Contains("remove-si-lu=OUI", tags);                 // D19 : chaque interrupteur ne touche que sa famille
        Assert.Contains("propager-lu=OUI", tags);
        Assert.Contains("favori", tags);
        Assert.Single(tags, t => t.StartsWith("propager-avancement", StringComparison.Ordinal));
        var entry = Assert.Single(r.Journal.Of("MarkerSet"));
        Assert.Equal($"family=propager-avancement value={journalValue} removed=1", entry.Detail);
    }

    [Fact]
    public void SetOption_DisablingPropagerLu_LeavesRemoveSiLuOuiInPlace_Inert_D21_Question2()
    {
        var r = new Rig();
        r.Shares.Add("p1", Owner, "À voir", 3, (MemberWrite, "Write"));
        r.Playlists.Playlists["p1"].Tags.AddRange(new[] { "remove-si-lu=OUI", "propager-lu=OUI", "propager-avancement=OUI" });

        var result = r.Service.SetOption(Owner, "p1", "propager-lu", false);

        Assert.True(result.Ok);
        Assert.Equal("Non", result.Value!.Options["propager-lu"]);
        Assert.Equal("Oui", result.Value.Options["remove-si-lu"]);            // pas basculé automatiquement
        Assert.Equal("Oui", result.Value.Options["propager-avancement"]);
    }

    [Fact]
    public void SetOption_RemoveSiLu_IsAcceptedByTheApi_EvenWhenPropagerLuIsNotActive_D21()
    {
        // L'API accepte toujours la bascule : la dépendance est appliquée par le moteur et signalée (grisage) par la page seulement.
        var r = new Rig();
        r.Shares.Add("p1", Owner, "À voir", 3, (MemberWrite, "Write"));
        r.Playlists.Playlists["p1"].Tags.AddRange(new[] { "remove-si-lu=NON", "propager-lu=NON", "propager-avancement=NON" });

        var result = r.Service.SetOption(Owner, "p1", "remove-si-lu", true);

        Assert.True(result.Ok);
        Assert.Equal("Oui", result.Value!.Options["remove-si-lu"]);
        Assert.Equal("Non", result.Value.Options["propager-lu"]);
    }

    [Theory]
    [InlineData("Propager-Avancement")]
    [InlineData("propager_avancement")]
    [InlineData("propager-avancement ")]
    [InlineData("avancement")]
    public void SetOption_PropagerAvancementVariants_AreInvalidFamily_ExactCaseSensitiveComparison_v120(string family)
    {
        var r = new Rig();
        r.Shares.Add("p1", Owner, "À voir", 3, (MemberWrite, "Write"));
        Assert.Equal(UserPageErrors.InvalidFamily, r.Service.SetOption(Owner, "p1", family, true).Error);
    }

    [Fact]
    public void SetOption_PropagerAvancement_OnANotSharedPlaylist_ReturnsNotShared_v120()
    {
        var r = new Rig();
        r.Shares.Add("p1", Owner, "À voir", 3);
        Assert.Equal(UserPageErrors.NotShared, r.Service.SetOption(Owner, "p1", "propager-avancement", true).Error);
    }
}
