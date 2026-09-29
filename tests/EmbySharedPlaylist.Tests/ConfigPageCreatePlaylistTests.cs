using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// Vérifications STATIQUES du script/page utilisateur pour la création de playlist (v1.2.0, #55, D22, tâche C4 ; maquette validée
/// `_work/mockup/v1.2.0/ui/user-page__v120.html`) : bouton « Nouvelle playlist », champ nom (composant natif `emby-input`, à AJOUTER au
/// `require`), messages FR/EN des erreurs sous le champ. Le rendu réel (composant natif, insertion de la carte, focus) est vérifié en QUALIF
/// (tests/integration/MANUAL.md §5septies, inspection DevTools). ROUGES tant que le lot C n'est pas implémenté (attendu).
/// </summary>
public class ConfigPageCreatePlaylistTests
{
    private static readonly Assembly PluginAssembly = typeof(Plugin).Assembly;

    private static string Read(string name)
    {
        using var stream = PluginAssembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string Js => Read("EmbySharedPlaylist.Configuration.userScript.js");
    private static string Html => Read("EmbySharedPlaylist.Configuration.userPage.html");
    private static readonly Regex FrBlock = new(@"\bfr\s*:\s*\{([^{}]*)\}", RegexOptions.Singleline);
    private static readonly Regex EnBlock = new(@"\ben\s*:\s*\{([^{}]*)\}", RegexOptions.Singleline);

    [Fact]
    public void TheRequireLoadsTheNativeEmbyInput_OtherwiseTheFieldStaysARawHtmlInput()
    {
        // Leçon du GATE 4 de v1.1.0 : sans require explicite, l'élément reste un <input> brut.
        Assert.Matches(@"require\(\s*\[[^\]]*['""]emby-input['""]", Js);
    }

    [Fact]
    public void TheNativeToggleSelectAndButtonModulesAreStillRequired()
    {
        Assert.Matches(@"require\(\s*\[[^\]]*['""]emby-toggle['""][^\]]*\]", Js);
        Assert.Matches(@"require\(\s*\[[^\]]*['""]emby-button['""][^\]]*\]", Js);
    }

    [Fact]
    public void TheScriptPostsTheNameToTheCreationEndpoint()
    {
        Assert.Contains("SharedPlaylist/User/Playlists", Js);
        Assert.Matches(@"\bName\s*:", Js);
    }

    [Fact]
    public void TheNameFieldIsLimitedTo100Characters_ClientSide()
    {
        var all = (Js + Html).ToLowerInvariant();
        Assert.Matches(@"maxlength[""']?\s*[=:,]\s*[""']?100", all);
    }

    [Fact]
    public void TheFrenchLabelsOfTheCreationAreCarried()
    {
        var fr = FrBlock.Match(Js).Groups[1].Value;
        Assert.Contains("Nouvelle playlist", fr);
        Assert.Contains("Vous avez déjà une playlist portant ce nom", fr);       // texte du guide (docs/chronogrammes.md §9)
    }

    [Theory]
    [InlineData("invalid-name")]
    [InlineData("name-exists")]
    public void TheScriptTranslatesTheNewServerErrorCodes(string code)
    {
        Assert.Contains(code, Js);
    }

    [Fact]
    public void TheEnglishDictionaryHasAnAlreadyExistsMessage_NoRawKeyFallback()
    {
        var en = EnBlock.Match(Js).Groups[1].Value;
        Assert.Contains("already", en, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheCreationErrorsAreShownWithoutBlockingDialogsNorInnerHtml()
    {
        var js = Js;
        Assert.DoesNotContain("innerHTML", js);
        Assert.DoesNotContain("alert(", js);
        Assert.DoesNotContain("confirm(", js.Replace("confirmAction(", ""));   // confirmAction = module Emby natif (retrait d'un membre), pas window.confirm
    }

    [Fact]
    public void TheNameIsNeverLoggedByTheScript()
    {
        // Les noms sont des données utilisateur : jamais dans la console.
        Assert.DoesNotMatch(@"console\.\w+\([^)]*[nN]ame", Js);
    }

    [Fact]
    public void TheToggleCloningIsRestrictedToCheckboxes_TheNameTextFieldIsNeverCloned()
    {
        Assert.Contains("input[type=\"checkbox\"]", Js.Replace("'", "\""));
    }

    [Fact]
    public void TheCreateButtonIsDisabledDuringTheRequest_AndEnterIsIgnoredMeanwhile_NoDoubleSubmit_m1()
    {
        var js = Js;
        Assert.Matches(@"disabled", js);
        Assert.Matches(@"(keydown|keypress|keyup)", js);
        Assert.Matches(@"(Enter|keyCode\s*===?\s*13|which\s*===?\s*13)", js);
        Assert.Matches(@"(creating|inFlight|busy|pending|submitting)", js);     // un drapeau d'état d'une requête en cours
    }

    [Fact]
    public void TheScriptTranslatesLimitReached_InFrenchAndEnglish_WithoutTheNumber()
    {
        var js = Js;
        Assert.Contains("limit-reached", js);
        foreach (var block in new[] { FrBlock, EnBlock })
        {
            var m = Regex.Match(block.Match(js).Groups[1].Value, @"\w*limit\w*\s*:\s*(['""])(.*?)\1", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            Assert.True(m.Success, "message limit-reached absent d'un dictionnaire");
            Assert.NotEmpty(m.Groups[2].Value);
            Assert.DoesNotMatch(@"\d", m.Groups[2].Value);          // la page n'affiche jamais la valeur du quota (constante serveur unique)
        }
    }
}
