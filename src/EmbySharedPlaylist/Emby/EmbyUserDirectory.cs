using EmbySharedPlaylist.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;

namespace EmbySharedPlaylist.Emby;

/// <summary>
/// Adaptateur du port <see cref="IUserDirectory"/> (D20, v1.1.0, #39) sur <c>IUserManager</c>. Même appel que
/// <see cref="EmbyUserPolicyGateway.AllUserIds"/> (<c>GetUserList(UserQuery)</c>, filtre vide = tous les comptes) ;
/// « actif » = <c>!Policy.IsDisabled</c>. <see cref="CanShare"/> délègue à l'<see cref="IUserPolicyGateway"/> déjà
/// existant (#26) : aucune duplication de la lecture d'<c>AllowSharingPersonalItems</c>.
/// </summary>
public sealed class EmbyUserDirectory : IUserDirectory
{
    private readonly IUserManager _userManager;
    private readonly IUserPolicyGateway _policyGateway;

    public EmbyUserDirectory(IUserManager userManager, IUserPolicyGateway policyGateway)
    {
        _userManager = userManager;
        _policyGateway = policyGateway;
    }

    public IReadOnlyList<DirectoryUser> ListSelectable(string excludeUserId) =>
        _userManager.GetUserList(new UserQuery())
            .Where(u => !string.Equals(u.Id.ToString("N"), excludeUserId, StringComparison.Ordinal))
            .Select(ToDirectoryUser)
            .Where(d => d.Active)
            .ToList();

    public DirectoryUser? Find(string userId)
    {
        // qa-20260928-160840.md §1.2 (confirme security-audit-20260928-154256.md §2, FAIBLE) : un identifiant qui
        // n'a pas la forme d'un GUID fait lever GetUserById (constaté en réel : POST User/Playlists/{id}/Members
        // avec un TargetUserId malformé renvoyait 500 internal au lieu de 400 invalid-user). Même patron que
        // EmbyPlaylistGateway.cs/PlaylistEntryReader.cs pour le même appel SDK : un identifiant malformé est
        // simplement inconnu (contrat déjà « null si inconnu »), jamais une exception qui remonte jusqu'au
        // Guard générique de UserPlaylistService.
        User? user;
        try { user = _userManager.GetUserById(userId); }
        catch { return null; }
        return user == null ? null : ToDirectoryUser(user);
    }

    public bool CanShare(string userId) => _policyGateway.IsSharingEnabled(userId) ?? false;

    private DirectoryUser ToDirectoryUser(User user) =>
        new(user.Id.ToString("N"), user.Name, !_userManager.GetUserPolicy(user).IsDisabled);
}
