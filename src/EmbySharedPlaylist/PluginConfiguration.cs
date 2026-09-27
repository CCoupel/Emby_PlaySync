using EmbySharedPlaylist.Core;
using MediaBrowser.Model.Plugins;

namespace EmbySharedPlaylist;

public class PluginConfiguration : BasePluginConfiguration
{
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
