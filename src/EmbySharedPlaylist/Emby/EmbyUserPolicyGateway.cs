using EmbySharedPlaylist.Core;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;

namespace EmbySharedPlaylist.Emby;

/// <summary>
/// Adaptateur du port <see cref="IUserPolicyGateway"/> sur <c>IUserManager</c> : même mécanisme déjà validé faisable par
/// le spike U5 (v0.1.0, <c>_work/reports/qa-spike-20260926-144953.md</c>) — relecture complète de la <c>UserPolicy</c>
/// puis modification d'un seul champ, sans effet de bord sur les autres droits. Pas de garde d'accès (R8) : contrairement
/// à <c>EmbyUserDataGateway</c>, il s'agit du compte de l'utilisateur lui-même, pas d'un média tiers.
/// </summary>
public sealed class EmbyUserPolicyGateway : IUserPolicyGateway
{
    private readonly IUserManager _userManager;

    public EmbyUserPolicyGateway(IUserManager userManager) => _userManager = userManager;

    // GetUserList(UserQuery) plutôt que la propriété Users (obsolète) : filtre par défaut vide = TOUS les comptes,
    // désactivés ou masqués compris (D8 : aucune exception).
    public IReadOnlyList<string> AllUserIds() => _userManager.GetUserList(new UserQuery()).Select(u => u.Id.ToString("N")).ToList();

    public bool? IsSharingEnabled(string userId)
    {
        var user = _userManager.GetUserById(userId);
        return user == null ? null : _userManager.GetUserPolicy(user).AllowSharingPersonalItems;
    }

    public bool EnableSharingIfNeeded(string userId)
    {
        var user = _userManager.GetUserById(userId);
        if (user == null) return false;

        var policy = _userManager.GetUserPolicy(user);
        if (policy.AllowSharingPersonalItems) return false; // déjà actif : jamais réécrit, jamais révoqué

        policy.AllowSharingPersonalItems = true;
        _userManager.UpdateUserPolicy(user.InternalId, policy);
        return true;
    }
}
