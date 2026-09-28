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

    /// <summary>v1.1.0 (#39, D20) : nom de la page « PlaySync » du menu UTILISATEUR (EnableInUserMenu).</summary>
    public const string UserPageName = "PlaySyncUserPage";

    /// <summary>v1.1.0 (#39, D20) : nom du contrôleur JS de la page utilisateur.</summary>
    public const string UserScriptName = "PlaySyncUserScript";

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

    // v1.1.0 (#39) : renommage produit (dépôt CCoupel/Emby_PlaySync, marque/site déjà "PlaySync", entrée de menu
    // utilisateur déjà "PlaySync" depuis ce même lot) — décision initiale du plan de ne pas y toucher, remplacée
    // par une demande explicite de l'utilisateur. Ne renomme QUE ce qui est visible côté admin (Name, page de
    // config) : ni les classes/namespaces C#, ni AssemblyName/RootNamespace (EmbySharedPlaylist.dll inchangé),
    // ni le nom de la tâche planifiée, ni le message d'aide écrit dans les playlists (HelpText — un changement
    // de texte y déclencherait un remplacement V2→V3 sur toutes les playlists, hors périmètre de cette demande).
    public override string Name => "PlaySync";

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
                DisplayName          = "PlaySync",
                MenuSection          = "server",
                MenuIcon             = "playlist_play"
            },
            new PluginPageInfo
            {
                Name                 = ConfigScriptName,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configScript.js"
            },
            // v1.1.0 (#39, D20) : page « PlaySync » du menu UTILISATEUR — jamais EnableInMainMenu (ça, c'est la page
            // admin ci-dessus). Entrée visible pour TOUS les comptes (Q6b, GATE U13 : aucun masquage natif par
            // permission trouvé, spike U13 question b) ; la page elle-même détecte l'absence de permission
            // (403 sharing-disabled) et affiche un message dédié. MenuSection="user" : hypothèse du spike U13
            // (confirmé servi à un compte non-admin sans 404, placement visuel dans le menu non vérifié à l'œil —
            // _work/reports/spike-u13-verification-20260928-151423.md, point a1).
            new PluginPageInfo
            {
                Name                 = UserPageName,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.userPage.html",
                EnableInUserMenu     = true,
                DisplayName          = "PlaySync",
                MenuSection          = "user",
                MenuIcon             = "group"
            },
            new PluginPageInfo
            {
                Name                 = UserScriptName,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.userScript.js"
            }
        };
    }
}
