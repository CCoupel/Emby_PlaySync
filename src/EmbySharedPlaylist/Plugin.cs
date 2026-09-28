using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Emby;

namespace EmbySharedPlaylist;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
{
    /// <summary>Nom de la page de configuration : <c>/web/configurationpage?name=EmbySharedPlaylistConfig</c>.</summary>
    public const string ConfigPageName = "EmbySharedPlaylistConfig";

    /// <summary>Nom du contrôleur JS de la page (référencé par data-controller="__plugin/…").</summary>
    public const string ConfigScriptName = "EmbySharedPlaylistConfigScript";

    public static Plugin? Instance { get; private set; }

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    // Ne jamais changer ce GUID après le premier déploiement
    public override Guid Id => new Guid("9ebe814e-9438-42b8-aa57-feea1ae92451");

    /// <summary>Journalise chaque sauvegarde de la configuration (le SDK n'expose pas d'événement dédié).</summary>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        base.UpdateConfiguration(configuration);
        PluginRuntime.Log?.Info(LogFormat.ConfigSaved());
    }

    public override string Name => "Emby Shared Playlist";

    public override string Description => "Playlists « À voir » partagées : un média lu est retiré de la liste et marqué lu pour les membres du groupe.";

    /// <summary>Icône du plugin (#49) : création originale (D-c, v1.0.0), source vectorielle <c>icon.svg</c> versionnée
    /// à la racine du projet, embarquée en JPEG 320x180 (même format que la convention Emby_Badges).</summary>
    public ImageFormat ThumbImageFormat => ImageFormat.Jpg;

    public Stream GetThumbImage() => GetType().Assembly.GetManifestResourceStream($"{GetType().Namespace}.Configuration.thumb.jpg")!;

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name                 = ConfigPageName,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
                EnableInMainMenu     = true,
                DisplayName          = "Emby Shared Playlist",
                MenuSection          = "server",
                MenuIcon             = "playlist_play"
            },
            new PluginPageInfo
            {
                Name                 = ConfigScriptName,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configScript.js"
            }
        };
    }
}
