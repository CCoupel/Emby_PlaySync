namespace EmbySharedPlaylist.Core;

/// <summary>
/// Port de lecture/écriture du flag lu d'un utilisateur (pour la propagation, #20). Ids en chaîne (GUID pour l'utilisateur,
/// entier interne pour le média, comme le reste des ports). Adaptateur : <c>Emby/EmbyUserDataGateway</c>.
/// </summary>
public interface IUserDataGateway
{
    /// <summary>
    /// Vrai/faux si l'utilisateur a accès au média ; <c>null</c> si l'utilisateur est inconnu ou n'a pas accès (bibliothèque,
    /// contrôle parental) — traité comme R8 : ignoré silencieusement, jamais une erreur.
    /// </summary>
    bool? IsPlayed(string userId, string itemId);

    /// <summary>
    /// Pose <c>Played=true</c> pour cet utilisateur (marque plugin, anti-écho #21 : à enregistrer via <see cref="PluginWriteTracker"/>
    /// AVANT cet appel). L'appelant doit avoir vérifié <see cref="IsPlayed"/> == false (jamais appelé si déjà lu, R7).
    /// Renvoie vrai si l'écriture a eu lieu (faux si l'utilisateur ou le média a disparu entre-temps).
    /// </summary>
    bool MarkPlayed(string userId, string itemId);
}
