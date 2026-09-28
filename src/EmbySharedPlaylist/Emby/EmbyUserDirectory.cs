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
        var user = _userManager.GetUserById(userId);
        return user == null ? null : ToDirectoryUser(user);
    }

    public bool CanShare(string userId) => _policyGateway.IsSharingEnabled(userId) ?? false;

    private DirectoryUser ToDirectoryUser(User user) =>
        new(user.Id.ToString("N"), user.Name, !_userManager.GetUserPolicy(user).IsDisabled);
}
