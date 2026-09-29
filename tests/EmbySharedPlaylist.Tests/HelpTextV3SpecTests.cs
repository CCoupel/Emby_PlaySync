using EmbySharedPlaylist.Reconciliation;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// Tests de SPÉCIFICATION de <c>HelpText.V3</c> (v1.2.0, D21, tâche B3 ; texte exact : docs/chronogrammes.md §1
/// « Message d'aide »). Trois lignes d'options, <c>propager-lu</c> EN PREMIER, la ligne <c>remove-si-lu</c> porte la
/// mention « (seulement si propager-lu=OUI) », la ligne <c>propager-avancement</c> précise « sans toucher au « lu » ».
/// V1 et V2 restent inchangés (comparaison caractère pour caractère pour le remplacement).
/// </summary>
public class HelpTextV3SpecTests
{
    // Reproduit tel quel depuis la spécification (les retours à la ligne sont des \n).
    private const string Expected =
        "Playlist partagée gérée par Emby Shared Playlist.\n" +
        "Trois étiquettes (Modifier les métadonnées > Mot-clé) règlent son comportement. Elles sont à NON par défaut : rien ne change.\n" +
        "- propager-lu=OUI : quand un membre passe un média à « lu », le « lu » est posé chez les autres membres.\n" +
        "- remove-si-lu=OUI : un média qui passe à « lu » est retiré de la playlist (seulement si propager-lu=OUI).\n" +
        "- propager-avancement=OUI : la position de lecture (pause, arrêt) est recopiée chez les autres membres, sans toucher au « lu ».\n" +
        "Pour activer une option, remplacez NON par OUI : ajoutez l'étiquette « ...=OUI » et retirez « ...=NON » (si les deux sont présentes, NON l'emporte).";

    [Fact]
    public void V3_IsExactlyTheTextOfTheSpecification() => Assert.Equal(Expected, HelpText.V3);

    [Fact]
    public void V3_ListsThePropagerLuLineFirst_ThenRemoveSiLu_ThenAvancement()
    {
        var options = HelpText.V3.Split('\n').Where(l => l.StartsWith("- ")).ToList();
        Assert.Equal(3, options.Count);
        Assert.StartsWith("- propager-lu=OUI", options[0]);
        Assert.StartsWith("- remove-si-lu=OUI", options[1]);
        Assert.StartsWith("- propager-avancement=OUI", options[2]);
    }

    [Fact]
    public void V3_StatesTheDependencyOfRemoveSiLuOnPropagerLu()
    {
        var line = HelpText.V3.Split('\n').Single(l => l.StartsWith("- remove-si-lu=OUI"));
        Assert.Contains("(seulement si propager-lu=OUI)", line);
    }

    [Fact]
    public void V3_StatesThatAvancementNeverTouchesTheReadFlag()
    {
        var line = HelpText.V3.Split('\n').Single(l => l.StartsWith("- propager-avancement=OUI"));
        Assert.Contains("sans toucher au « lu »", line);
    }

    [Fact]
    public void V3_MentionsThreeTagsAndTheNonWinsRule()
    {
        Assert.StartsWith("Playlist partagée gérée par Emby Shared Playlist.", HelpText.V3);
        Assert.Contains("Trois étiquettes", HelpText.V3);
        Assert.Contains("NON l'emporte", HelpText.V3);
        Assert.DoesNotContain("Deux étiquettes", HelpText.V3);
        Assert.DoesNotContain("fonction à venir", HelpText.V3);
    }

    [Fact]
    public void V3_HasNoPersonalOrSecretData()
    {
        foreach (var forbidden in new[] { "http", "token", "@", "\\", "/config" })
            Assert.DoesNotContain(forbidden, HelpText.V3, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V1V2V3_AreThreeDistinctTexts_V1AndV2Unchanged()
    {
        Assert.NotEqual(HelpText.V1, HelpText.V2);
        Assert.NotEqual(HelpText.V2, HelpText.V3);
        Assert.NotEqual(HelpText.V1, HelpText.V3);
        Assert.Contains("(fonction à venir)", HelpText.V1);
        Assert.Contains("l'avancement de lecture (position, pause) est aussi propagé", HelpText.V2);   // V2 : ancien couplage lu + avancement
        Assert.Equal(HelpText.V1, HelpText.Message);
    }
}
