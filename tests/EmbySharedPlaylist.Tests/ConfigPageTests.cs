using System.Reflection;
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
}
