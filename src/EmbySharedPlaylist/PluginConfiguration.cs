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
}
