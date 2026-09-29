namespace EmbySharedPlaylist.Core;

/// <summary>Compte du serveur, vu par l'annuaire de la page utilisateur (id + nom + actif seulement).</summary>
public sealed record DirectoryUser(string UserId, string Name, bool Active);

/// <summary>
/// Port de l'annuaire des utilisateurs pour la page utilisateur (D20, v1.1.0, #39 — adaptateur :
/// <c>Emby/EmbyUserDirectory</c>, Phase 2). Champs strictement limités (décision Q5bis) : jamais de rôle admin, de
/// politique ou de date au-delà de <see cref="DirectoryUser.Active"/>.
/// </summary>
public interface IUserDirectory
{
    /// <summary>Comptes actifs, hors <paramref name="excludeUserId"/> (sélecteur — exclut toujours le demandeur).</summary>
    IReadOnlyList<DirectoryUser> ListSelectable(string excludeUserId);

    /// <summary><c>null</c> si l'identifiant est inconnu.</summary>
    DirectoryUser? Find(string userId);

    /// <summary>Délègue à <see cref="IUserPolicyGateway.IsSharingEnabled"/> (porte d'entrée commune à tous les
    /// endpoints <c>User/*</c>, 403 <c>sharing-disabled</c> sinon). Faux si l'utilisateur est inconnu.</summary>
    bool CanShare(string userId);
}
