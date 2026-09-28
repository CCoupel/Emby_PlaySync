using System.Reflection;
using System.Text.RegularExpressions;
using EmbySharedPlaylist;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>La page de config est servie sous le nom EmbySharedPlaylistConfig : nom, ressources embarquées et contrôleur JS cohérents.</summary>
public class ConfigPageTests
{
    private static readonly Assembly PluginAssembly = typeof(Plugin).Assembly;

    private static string ReadResource(string name)
    {
        using var stream = PluginAssembly.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }

    [Fact]
    public void PageName_IsEmbySharedPlaylistConfig() =>
        Assert.Equal("EmbySharedPlaylistConfig", Plugin.ConfigPageName);

    [Fact]
    public void EmbeddedResources_ExistUnderPluginNamespace()
    {
        var names = PluginAssembly.GetManifestResourceNames();
        Assert.Contains("EmbySharedPlaylist.Configuration.configPage.html", names);
        Assert.Contains("EmbySharedPlaylist.Configuration.configScript.js", names);
        Assert.Contains("EmbySharedPlaylist.Configuration.thumb.jpg", names);
    }

    [Fact]
    public void ThumbImage_IsAValidNonEmptyJpeg()
    {
        // Même nom que Plugin.GetThumbImage() (#49, v1.0.0) : ce test ne construit pas Plugin (SDK requis pour son
        // constructeur), mais vérifie exactement ce que GetThumbImage() renvoie (GetType().Assembly.GetManifestResourceStream).
        using var stream = PluginAssembly.GetManifestResourceStream("EmbySharedPlaylist.Configuration.thumb.jpg");
        Assert.NotNull(stream);
        var bytes = new byte[4];
        Assert.Equal(4, stream!.Read(bytes, 0, 4));
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF }, bytes[..3]); // signature JPEG (SOI + marqueur)
        Assert.True(stream.Length > 1000); // pas un fichier vide/tronqué
    }

    [Fact]
    public void ConfigPage_DeclaresTheScriptControllerServedByThePlugin()
    {
        var html = ReadResource("EmbySharedPlaylist.Configuration.configPage.html");
        Assert.Contains("data-controller=\"__plugin/" + Plugin.ConfigScriptName + "\"", html);
    }

    [Theory]
    [InlineData("GracePasses")]
    [InlineData("EnableDiagnostics")]
    [InlineData("LogToConsole")]
    [InlineData("LogLevel")]
    [InlineData("AutoEnableSharing")]
    public void ConfigPageAndScript_ExposeTheV020AndV040Parameters(string id)
    {
        Assert.Contains("id=\"" + id + "\"", ReadResource("EmbySharedPlaylist.Configuration.configPage.html"));
        Assert.Contains("#" + id, ReadResource("EmbySharedPlaylist.Configuration.configScript.js"));
    }

    [Fact]
    public void AutoEnableSharing_IsACheckbox_LikeTheOtherBooleanParameters()
    {
        var html = ReadResource("EmbySharedPlaylist.Configuration.configPage.html");
        Assert.Contains("<input type=\"checkbox\" id=\"AutoEnableSharing\" is=\"emby-checkbox\" class=\"emby-checkbox\" />", html);
    }

    [Fact]
    public void HelpPanel_PresentsThePermissionAsOptIn_DisabledByDefault_GateProdM1()
    {
        // v1.0.0 (GATE PROD, security-20260927-221434.md M1) : AutoEnableSharing est désactivé par défaut, l'encart
        // ne doit plus présenter la permission comme déjà acquise sans action de l'administrateur (contrairement au
        // texte v0.4.0-v0.5.0, où le défaut était actif).
        var html = ReadResource("EmbySharedPlaylist.Configuration.configPage.html");
        Assert.Contains("remove-si-lu", html);
        Assert.Contains("propager-lu", html);
        Assert.Contains("Gérer la collaboration", html);
        Assert.Contains("désactivée par défaut", html);
        Assert.DoesNotContain("déjà accordée automatiquement", html);
    }

    [Fact]
    public void ConfigPage_OffersTheThreeLogLevels()
    {
        var html = ReadResource("EmbySharedPlaylist.Configuration.configPage.html");
        foreach (var v in new[] { "Off", "Info", "Debug" }) Assert.Contains("<option value=\"" + v + "\">", html);
    }

    [Fact]
    public void ConfigScript_UsesThePluginGuid()
    {
        var js = ReadResource("EmbySharedPlaylist.Configuration.configScript.js");
        Assert.Contains("9ebe814e-9438-42b8-aa57-feea1ae92451", js);
    }

    // ==============================================================================================================
    // Extension v1.1.0 (#39, plan `_work/reports/plan-20260928-143007.md` tâche 8, contrats « Page utilisateur »,
    // docs/chronogrammes.md D20) : page « PlaySync » du menu UTILISATEUR (EnableInUserMenu), FR/EN, bilingue.
    // ADDITIF UNIQUEMENT (règle 6 non-régression du test-writer) : aucune méthode ci-dessus n'est modifiée.
    //
    // Contrat proposé pour dev-plugin (Plugin.cs) : mêmes constantes/patron que ConfigPageName/ConfigScriptName —
    //   public const string UserPageName = "PlaySyncUserPage";
    //   public const string UserScriptName = "PlaySyncUserScript";
    // Ressources embarquées : EmbySharedPlaylist.Configuration.userPage.html / .userScript.js (mêmes dossier/convention
    // que configPage.html/configScript.js). GetPages() (SDK requis, non testable ici comme pour ConfigPageName déjà) :
    // EnableInUserMenu=true, DisplayName="PlaySync" — vérifié par code-reviewer/QA (REST + manuel), pas ici.
    //
    // Dictionnaire FR/EN (CA7) : convention minimale attendue dans userScript.js — un littéral JS de la forme
    // `fr: { CLE: "...", ... }` et `en: { CLE: "...", ... }` (objets plats, un niveau), mêmes clés dans les deux
    // langues. Si dev-plugin adopte une autre structure, le signaler au teamleader (écart de contrat interne, pas
    // fonctionnel) plutôt que de le faire échouer silencieusement.
    // ==============================================================================================================

    private static readonly Regex FrBlock = new(@"\bfr\s*:\s*\{([^{}]*)\}", RegexOptions.Singleline);
    private static readonly Regex EnBlock = new(@"\ben\s*:\s*\{([^{}]*)\}", RegexOptions.Singleline);
    private static readonly Regex DictKey = new(@"(?:^|[,{\s])([A-Za-z_][A-Za-z0-9_-]*)\s*:", RegexOptions.Multiline);

    private static IReadOnlyList<string> KeysOf(Regex block, string js)
    {
        var m = block.Match(js);
        Assert.True(m.Success, $"bloc introuvable ({block}) dans userScript.js — voir convention documentée sur ConfigPageTests");
        return DictKey.Matches(m.Groups[1].Value).Select(k => k.Groups[1].Value).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void UserPageName_IsPlaySyncUserPage()
    {
        Assert.Equal("PlaySyncUserPage", Plugin.UserPageName);
    }

    [Fact]
    public void UserScriptName_IsPlaySyncUserScript()
    {
        Assert.Equal("PlaySyncUserScript", Plugin.UserScriptName);
    }

    [Fact]
    public void EmbeddedResources_IncludeTheUserPageAndScript()
    {
        var names = PluginAssembly.GetManifestResourceNames();
        Assert.Contains("EmbySharedPlaylist.Configuration.userPage.html", names);
        Assert.Contains("EmbySharedPlaylist.Configuration.userScript.js", names);
    }

    [Fact]
    public void UserPage_DeclaresTheScriptControllerServedByThePlugin()
    {
        var html = ReadResource("EmbySharedPlaylist.Configuration.userPage.html");
        Assert.Contains("data-controller=\"__plugin/" + Plugin.UserScriptName + "\"", html);
    }

    [Fact]
    public void UserScript_DetectsLanguageFromTheClient_FallsBackToEnglishOutsideFrench()
    {
        // CA7 : langue du client Emby / navigator.language, repli EN hors "fr*".
        var js = ReadResource("EmbySharedPlaylist.Configuration.userScript.js");
        Assert.Contains("navigator.language", js);
        Assert.Contains("\"fr\"", js.Replace('\'', '"')); // teste la présence du code langue "fr", quels que soient les guillemets utilisés
    }

    [Fact]
    public void UserScript_FrenchAndEnglishDictionaries_HaveExactlyTheSameKeys()
    {
        var js = ReadResource("EmbySharedPlaylist.Configuration.userScript.js");
        var fr = KeysOf(FrBlock, js);
        var en = KeysOf(EnBlock, js);
        Assert.NotEmpty(fr);
        Assert.Equal(fr, en); // mêmes clés, dans les deux langues (déjà triées à l'identique) : aucune traduction manquante
    }

    [Theory]
    [InlineData("sharing-disabled")]
    [InlineData("not-found")]
    [InlineData("invalid-level")]
    [InlineData("invalid-user")]
    [InlineData("self")]
    [InlineData("not-shared")]
    [InlineData("busy")]
    [InlineData("invalid-family")]
    public void UserScript_TranslatesEveryServerErrorCode_CA7(string code)
    {
        // Les codes d'erreur serveur (contracts/http-endpoints.md, corps {"Error":"<code>"}) sont stables et NON
        // localisés côté serveur : la page doit les reconnaître pour les traduire (toast, CA7).
        var js = ReadResource("EmbySharedPlaylist.Configuration.userScript.js");
        Assert.Contains(code, js);
    }

    [Fact]
    public void UserScript_NeverUsesInnerHtml_NamesAreUntrustedServerData()
    {
        // Tâche 8 du plan : noms de comptes/playlists saisis par des utilisateurs -> XSS si insérés via innerHTML.
        // Exigence la plus sûre et la plus simple à vérifier statiquement : AUCUNE occurrence d'innerHTML.
        var js = ReadResource("EmbySharedPlaylist.Configuration.userScript.js");
        Assert.DoesNotContain("innerHTML", js);
    }

    [Fact]
    public void UserPageAndScript_NeverUseBlockingBrowserDialogs()
    {
        // Toujours des composants Emby natifs (emby-select/emby-button/emby-toggle, dialogue/toast Emby) : jamais
        // alert()/confirm() (bloquants, non traduits, hors charte).
        var html = ReadResource("EmbySharedPlaylist.Configuration.userPage.html");
        var js = ReadResource("EmbySharedPlaylist.Configuration.userScript.js");
        foreach (var forbidden in new[] { "alert(", "confirm(", "window.alert", "window.confirm" })
        {
            Assert.DoesNotContain(forbidden, html);
            Assert.DoesNotContain(forbidden, js);
        }
    }

    [Fact]
    public void UserPage_NeverExposesManageOrManageDeleteAsASelectableLevel()
    {
        // CA6/R2 : seuls Read/Write sont proposés dans le sélecteur de niveau (jamais Manage/ManageDelete côté UI,
        // cohérent avec le refus serveur invalid-level).
        var html = ReadResource("EmbySharedPlaylist.Configuration.userPage.html");
        var js = ReadResource("EmbySharedPlaylist.Configuration.userScript.js");
        Assert.DoesNotContain("ManageDelete", html);
        Assert.DoesNotContain("ManageDelete", js);
        // "Manage" seul (sans Delete) : accepté seulement s'il n'apparaît jamais comme value d'option (contrôle large,
        // volontairement permissif sur un simple commentaire ou nom de variable contenant "Manage").
        Assert.DoesNotContain("value=\"Manage\"", html);
        Assert.DoesNotContain("\"Manage\"", js.Replace('\'', '"'));
    }
}
