using EmbySharedPlaylist.Core;
using MediaBrowser.Model.Plugins;

namespace EmbySharedPlaylist;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Active les endpoints de diagnostic <c>/SharedPlaylist/Spike/*</c> (temporaires, v0.1.0).
    /// Désactivé par défaut : les endpoints répondent alors 404.
    /// </summary>
    public bool EnableSpikeEndpoints { get; set; } = false;

    /// <summary>
    /// Active la sonde temporaire de réentrance U11 (B52, retirée avec Spike/*) : le scénario est choisi par le préfixe
    /// du nom de la playlist (SPIKE-P1… à SPIKE-P6…). Comptes test_* et playlists SPIKE* uniquement. Défaut : faux.
    /// </summary>
    public bool EnableReentrancyProbe { get; set; } = false;

    /// <summary>Nombre de passes de réconciliation consécutives sans étiquette (ou avec description vide) avant repose. Défaut 2, minimum 1.</summary>
    public int GracePasses { get; set; } = 2;

    /// <summary>Utilisé par le plugin : <see cref="GracePasses"/> borné à 1 minimum.</summary>
    public int EffectiveGracePasses => Math.Max(1, GracePasses);

    /// <summary>Active <c>Diagnostics/*</c> (admin, lecture seule ; 404 si faux). Défaut vrai.</summary>
    public bool EnableDiagnostics { get; set; } = true;

    /// <summary>Écrit aussi les lignes du plugin sur la console du pod (<c>kubectl logs</c>). Défaut vrai.</summary>
    public bool LogToConsole { get; set; } = true;

    /// <summary>Off | Info | Debug. Défaut Info. Aucun état de fonctionnement n'est stocké dans la configuration.</summary>
    public LogLevel LogLevel { get; set; } = LogLevel.Info;
}
