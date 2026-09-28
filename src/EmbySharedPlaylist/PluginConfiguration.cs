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

    /// <summary>
    /// Pose automatiquement <c>Policy.AllowSharingPersonalItems=true</c> pour tous les utilisateurs, existants et
    /// nouveaux (#26). Désactivé : aucune écriture, mais ne révoque JAMAIS un accès déjà accordé.
    /// <para>
    /// Défaut FAUX depuis v1.0.0 (GATE PROD, décision utilisateur explicite, réf.
    /// <c>security-20260927-221434.md</c> M1) : un serveur PROD peut être exposé publiquement sur Internet, un
    /// élargissement de droits automatique pour tous les comptes n'y est plus acceptable par défaut — à activer
    /// consciemment par l'administrateur. Défaut vrai en v0.4.0/v0.5.0 (QUALIF non exposé) ; ce changement ne
    /// modifie qu'une configuration VIERGE (aucun impact sur une instance où la valeur a déjà été sauvegardée,
    /// explicitement ou via tout enregistrement de la page de configuration — sérialisation XML standard d'Emby).
    /// </para>
    /// </summary>
    public bool AutoEnableSharing { get; set; } = false;
}
