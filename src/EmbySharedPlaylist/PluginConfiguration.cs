using MediaBrowser.Model.Plugins;

namespace EmbySharedPlaylist;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Active les endpoints de diagnostic <c>/SharedPlaylist/Spike/*</c> (temporaires, v0.1.0).
    /// Désactivé par défaut : les endpoints répondent alors 404.
    /// </summary>
    public bool EnableSpikeEndpoints { get; set; } = false;
}
