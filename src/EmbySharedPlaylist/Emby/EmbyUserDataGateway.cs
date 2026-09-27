using EmbySharedPlaylist.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace EmbySharedPlaylist.Emby;

/// <summary>
/// Adaptateur du port <see cref="IUserDataGateway"/> sur le SDK Emby : <c>IUserDataManager</c> pour le flag, combiné à
/// <c>BaseItem.IsVisibleStandalone(User)</c> pour l'accès bibliothèque/contrôle parental (R8). Écriture dans un
/// <see cref="WriteScope"/> (la garde anti-écho principale reste <see cref="PluginWriteTracker"/>, enregistrée par l'appelant).
/// </summary>
public sealed class EmbyUserDataGateway : IUserDataGateway
{
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;

    public EmbyUserDataGateway(IUserManager userManager, ILibraryManager libraryManager, IUserDataManager userDataManager)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
    }

    public bool? IsPlayed(string userId, string itemId)
    {
        var user = FindUser(userId);
        var item = FindItem(itemId);
        if (user == null || item == null) return null;
        if (!item.IsVisibleStandalone(user)) return null; // R8 : pas d'accès (bibliothèque, contrôle parental)
        return _userDataManager.GetUserData(user, item).Played;
    }

    public bool MarkPlayed(string userId, string itemId)
    {
        var user = FindUser(userId);
        var item = FindItem(itemId);
        if (user == null || item == null) return false;

        var data = _userDataManager.GetUserData(user, item);
        data.Played = true;
        if (data.PlayCount < 1) data.PlayCount = 1;
        data.LastPlayedDate = DateTimeOffset.UtcNow;
        using (WriteScope.Enter())
        {
            _userDataManager.SaveUserData(user, item, data, UserDataSaveReason.TogglePlayed, CancellationToken.None);
        }
        return true;
    }

    private User? FindUser(string userId) => _userManager.GetUserById(userId);

    private BaseItem? FindItem(string itemId) => long.TryParse(itemId, out var id) ? _libraryManager.GetItemById(id) : null;
}
