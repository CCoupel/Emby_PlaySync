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
    }

    [Fact]
    public void ConfigPage_DeclaresTheScriptControllerServedByThePlugin()
    {
        var html = ReadResource("EmbySharedPlaylist.Configuration.configPage.html");
        Assert.Contains("data-controller=\"__plugin/" + Plugin.ConfigScriptName + "\"", html);
    }

    [Fact]
    public void ConfigScript_UsesThePluginGuid()
    {
        var js = ReadResource("EmbySharedPlaylist.Configuration.configScript.js");
        Assert.Contains("9ebe814e-9438-42b8-aa57-feea1ae92451", js);
    }
}
