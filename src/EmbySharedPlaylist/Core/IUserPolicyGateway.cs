namespace EmbySharedPlaylist.Core;

/// <summary>
/// Port symétrique de <see cref="IPlaylistGateway"/>/<see cref="IUserDataGateway"/> : accès borné à
/// <c>Policy.AllowSharingPersonalItems</c> (#26). Aucune révocation possible via ce port (cohérent avec D-e/D11 :
/// aucune mémoire d'un décochage manuel, le plugin ne fait jamais que « replacer ce qui manque »).
/// </summary>
public interface IUserPolicyGateway
{
    /// <summary>Tous les identifiants d'utilisateurs connus (adaptateur Emby : <c>IUserManager.Users</c>).</summary>
    IReadOnlyList<string> AllUserIds();

    /// <summary>Lecture seule (diagnostic/tests) ; null si utilisateur inconnu.</summary>
    bool? IsSharingEnabled(string userId);

    /// <summary>
    /// Relit la <c>UserPolicy</c> complète, modifie UNIQUEMENT <c>AllowSharingPersonalItems</c> si elle est actuellement
    /// fausse ; aucun autre champ touché. Jamais de révocation (no-op si déjà vraie).
    /// </summary>
    /// <returns>Vrai si une écriture a eu lieu, faux si déjà activée ou utilisateur inconnu.</returns>
    bool EnableSharingIfNeeded(string userId);
}
