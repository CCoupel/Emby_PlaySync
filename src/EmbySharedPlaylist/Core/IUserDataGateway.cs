namespace EmbySharedPlaylist.Core;

/// <summary>
/// Port de lecture/écriture des données utilisateur d'un AUTRE utilisateur : flag lu (propagation, #20) et position de
/// lecture (avancement, #45). Ids en chaîne (GUID pour l'utilisateur, entier interne pour le média, comme le reste des
/// ports). Adaptateur : <c>Emby/EmbyUserDataGateway</c>.
/// </summary>
public interface IUserDataGateway
{
    /// <summary>
    /// Vrai si l'utilisateur a accès au média (bibliothèque, contrôle parental) ; faux si inconnu ou sans accès — traité
    /// comme R8 : ignoré silencieusement, jamais une erreur. Extrait de <see cref="IsPlayed"/> (même vérification).
    /// </summary>
    bool HasAccess(string userId, string itemId);

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

    /// <summary><c>PlaybackPositionTicks</c> actuel de l'utilisateur pour ce média ; <c>null</c> si inconnu ou sans accès (R8).</summary>
    long? GetPosition(string userId, string itemId);

    /// <summary>
    /// Pose <c>PlaybackPositionTicks</c> et <c>LastPlayedDate</c> UNIQUEMENT (jamais <c>Played</c> ni <c>PlayCount</c> : #45,
    /// distinct de <see cref="MarkPlayed"/>). Anti-écho #21 : à enregistrer via <see cref="PluginWriteTracker"/> AVANT cet
    /// appel. Ne fait rien (renvoie faux) si l'utilisateur n'a pas accès (R8) ou si la position est déjà celle demandée
    /// (pas d'écriture inutile) ; renvoie vrai si l'écriture a eu lieu.
    /// </summary>
    bool SetPosition(string userId, string itemId, long ticks);
}
