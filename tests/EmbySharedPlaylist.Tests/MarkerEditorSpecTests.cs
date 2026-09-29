using EmbySharedPlaylist.Marker;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// Tests de SPÉCIFICATION de <c>EmbySharedPlaylist.Marker.MarkerEditor</c> (D19, v1.1.0, #39, docs/chronogrammes.md
/// §D19 + contracts/http-endpoints.md « POST User/Playlists/{id}/Options ») : fonction PURE, écrite avant le code
/// (plan `_work/reports/plan-20260928-143007.md`, tâche 2). Signature attendue :
/// <c>MarkerEditor.Replace(IReadOnlyList&lt;string&gt; tags, MarkerFamily family, bool enabled) -&gt;
/// (IReadOnlyList&lt;string&gt; NewTags, int Removed)</c> — remplace ATOMIQUEMENT toutes les étiquettes reconnues de la
/// famille (même motif que <see cref="MarkerEvaluator"/>) par une seule étiquette canonique, préserve l'ordre des
/// autres étiquettes, idempotent, aucune écriture (le gateway relit/réécrit ; voir <c>UserPlaylistServiceSpecTests</c>
/// pour la chaîne complète sous verrou).
/// </summary>
public class MarkerEditorSpecTests
{
    private static string Canon(MarkerFamily f, bool enabled) => MarkerEvaluator.FamilyName(f) + "=" + (enabled ? "OUI" : "NON");

    [Fact]
    public void Replace_NonToOui_RemovesTheSingleNonTag_AddsCanonicalOui()
    {
        var (tags, removed) = MarkerEditor.Replace(new[] { "remove-si-lu=NON" }, MarkerFamily.RemoveSiLu, enabled: true);
        Assert.Equal(new[] { "remove-si-lu=OUI" }, tags);
        Assert.Equal(1, removed);
    }

    [Fact]
    public void Replace_OuiToNon_RemovesTheSingleOuiTag_AddsCanonicalNon()
    {
        var (tags, removed) = MarkerEditor.Replace(new[] { "remove-si-lu=OUI" }, MarkerFamily.RemoveSiLu, enabled: false);
        Assert.Equal(new[] { "remove-si-lu=NON" }, tags);
        Assert.Equal(1, removed);
    }

    [Fact]
    public void Replace_ConflictOuiAndNon_RemovesBoth_Removed2_AddsCanonicalOnce()
    {
        // S10.3 (docs/chronogrammes.md) : édition manuelle qui a laissé OUI + NON coexister ; la bascule (même valeur
        // ou non) résout le conflit en une seule étiquette canonique, removed=2.
        var (tags, removed) = MarkerEditor.Replace(new[] { "remove-si-lu=OUI", "remove-si-lu=NON" }, MarkerFamily.RemoveSiLu, enabled: true);
        Assert.Equal(new[] { "remove-si-lu=OUI" }, tags);
        Assert.Equal(2, removed);
    }

    [Theory]
    [InlineData("Remove-Si-Lu=oui")]
    [InlineData("  remove-si-lu = OUI  ")]
    [InlineData("REMOVE-SI-LU=NON")]
    public void Replace_RecognizesCaseAndSpaceVariants_SameAsMarkerEvaluator(string variant)
    {
        var (tags, removed) = MarkerEditor.Replace(new[] { variant }, MarkerFamily.RemoveSiLu, enabled: true);
        Assert.Equal(new[] { "remove-si-lu=OUI" }, tags);
        Assert.Equal(1, removed);
    }

    [Fact]
    public void Replace_ForeignTagsAndOtherFamily_AreUntouched_OrderPreserved()
    {
        var input = new[] { "genre=horreur", "remove-si-lu=NON", "propager-lu=OUI", "favori" };
        var (tags, removed) = MarkerEditor.Replace(input, MarkerFamily.RemoveSiLu, enabled: true);
        Assert.Equal(new[] { "genre=horreur", "propager-lu=OUI", "favori", "remove-si-lu=OUI" }, tags);
        Assert.Equal(1, removed);
    }

    [Fact]
    public void Replace_NeighbourVariants_AreNotTouchedByTheReplacement()
    {
        // "remove-si-lu=OUIX" et "remove-si-lu-oui" ne sont PAS des étiquettes de la famille (MarkerEvaluator) :
        // elles restent intactes, et ne comptent pas dans removed.
        var input = new[] { "remove-si-lu=OUIX", "remove-si-lu-oui", "remove-si-lu=NON" };
        var (tags, removed) = MarkerEditor.Replace(input, MarkerFamily.RemoveSiLu, enabled: true);
        Assert.Equal(new[] { "remove-si-lu=OUIX", "remove-si-lu-oui", "remove-si-lu=OUI" }, tags);
        Assert.Equal(1, removed);
    }

    [Fact]
    public void Replace_FamilyAbsent_AddsOnly_Removed0()
    {
        var (tags, removed) = MarkerEditor.Replace(new[] { "favori" }, MarkerFamily.PropagerLu, enabled: false);
        Assert.Equal(new[] { "favori", "propager-lu=NON" }, tags);
        Assert.Equal(0, removed);
    }

    [Fact]
    public void Replace_IsIdempotent_ReapplyingTheSameStateChangesNothingButTheCount()
    {
        var (first, removed1) = MarkerEditor.Replace(new[] { "remove-si-lu=NON" }, MarkerFamily.RemoveSiLu, enabled: true);
        var (second, removed2) = MarkerEditor.Replace(first, MarkerFamily.RemoveSiLu, enabled: true);
        Assert.Equal(first, second);
        Assert.Equal(1, removed1);
        Assert.Equal(1, removed2); // la canonique déjà posée est retirée puis reposée à l'identique (atomique, pas de court-circuit)
    }

    [Fact]
    public void Replace_TwoFamiliesAreIndependent_TogglingOneNeverTouchesTheOther()
    {
        var input = new[] { "remove-si-lu=OUI", "propager-lu=OUI" };
        var (tags, removed) = MarkerEditor.Replace(input, MarkerFamily.RemoveSiLu, enabled: false);
        Assert.Equal(new[] { "propager-lu=OUI", "remove-si-lu=NON" }, tags);
        Assert.Equal(1, removed);
    }

    [Fact]
    public void Replace_EmptyTagList_AddsCanonicalOnly()
    {
        var (tags, removed) = MarkerEditor.Replace(Array.Empty<string>(), MarkerFamily.RemoveSiLu, enabled: true);
        Assert.Equal(new[] { "remove-si-lu=OUI" }, tags);
        Assert.Equal(0, removed);
    }

    [Theory]
    [InlineData(MarkerFamily.RemoveSiLu, true)]
    [InlineData(MarkerFamily.RemoveSiLu, false)]
    [InlineData(MarkerFamily.PropagerLu, true)]
    [InlineData(MarkerFamily.PropagerLu, false)]
    [InlineData(MarkerFamily.PropagerAvancement, true)]    // v1.2.0 (D21)
    [InlineData(MarkerFamily.PropagerAvancement, false)]
    public void Replace_AlwaysAddsExactlyOneCanonicalTag_ForTheRequestedFamily(MarkerFamily family, bool enabled)
    {
        var (tags, _) = MarkerEditor.Replace(new[] { "remove-si-lu=OUI", "propager-lu=NON" }, family, enabled);
        Assert.Equal(1, tags.Count(t => t == Canon(family, enabled)));
        Assert.Contains(Canon(family, enabled), tags);
    }

    // ---- v1.2.0 (D21) : troisième famille, ajout ADDITIF (aucun test existant modifié hors la table ci-dessus) ----------

    [Fact]
    public void Replace_PropagerAvancement_NonToOui_TouchesOnlyItsOwnFamily()
    {
        var input = new[] { "remove-si-lu=OUI", "propager-lu=NON", "propager-avancement=NON", "favori" };
        var (tags, removed) = MarkerEditor.Replace(input, MarkerFamily.PropagerAvancement, enabled: true);
        Assert.Equal(new[] { "remove-si-lu=OUI", "propager-lu=NON", "favori", "propager-avancement=OUI" }, tags);
        Assert.Equal(1, removed);
    }

    [Fact]
    public void Replace_ThreeFamiliesAreIndependent_DisablingPropagerLu_LeavesRemoveSiLuAndAvancementUntouched()
    {
        // D21 / question 2 du plan : désactiver « Propager le lu » laisse remove-si-lu=OUI en place (inerte), sans le basculer.
        var input = new[] { "remove-si-lu=OUI", "propager-lu=OUI", "propager-avancement=OUI" };
        var (tags, removed) = MarkerEditor.Replace(input, MarkerFamily.PropagerLu, enabled: false);
        Assert.Equal(new[] { "remove-si-lu=OUI", "propager-avancement=OUI", "propager-lu=NON" }, tags);
        Assert.Equal(1, removed);
    }

    [Theory]
    [InlineData("propager-avancement=OUI")]
    [InlineData("PROPAGER-AVANCEMENT = non")]
    public void Replace_PropagerAvancement_RecognisesCaseAndSpaces(string existing)
    {
        var (tags, removed) = MarkerEditor.Replace(new[] { existing }, MarkerFamily.PropagerAvancement, enabled: true);
        Assert.Equal(new[] { "propager-avancement=OUI" }, tags);
        Assert.Equal(1, removed);
    }

    [Fact]
    public void Replace_PropagerLu_NeverTouchesAPropagerAvancementTag_AndViceVersa()
    {
        // Les noms partagent le préfixe « propager- » : aucune famille ne doit reconnaître l'étiquette de l'autre.
        var (a, ra) = MarkerEditor.Replace(new[] { "propager-avancement=OUI" }, MarkerFamily.PropagerLu, enabled: true);
        Assert.Equal(new[] { "propager-avancement=OUI", "propager-lu=OUI" }, a);
        Assert.Equal(0, ra);
        var (b, rb) = MarkerEditor.Replace(new[] { "propager-lu=OUI" }, MarkerFamily.PropagerAvancement, enabled: true);
        Assert.Equal(new[] { "propager-lu=OUI", "propager-avancement=OUI" }, b);
        Assert.Equal(0, rb);
    }
}
