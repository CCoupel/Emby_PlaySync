using EmbySharedPlaylist.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.Logging;

namespace EmbySharedPlaylist.Emby;

/// <summary>
/// Pose la permission de partage (#26) IMMÉDIATEMENT à la création d'un utilisateur (<c>IUserManager.UserCreated</c>),
/// sans attendre la prochaine passe de réconciliation planifiée (repli : au plus 5 min de retard sinon). Symétrique de
/// <see cref="PlaybackListener"/>/<see cref="PlaybackSessionListener"/>/<see cref="PlaylistEventsListener"/>. Try/catch
/// total : aucune exception n'atteint le pipeline d'Emby.
/// </summary>
public sealed class UserPolicyListener : IServerEntryPoint
{
    private readonly IUserManager _userManager;

    public UserPolicyListener(IUserManager userManager, ILibraryManager libraryManager, IItemRepository itemRepository,
        IPlaylistManager playlistManager, IProviderManager providerManager, IUserDataManager userDataManager, ILogManager logManager)
    {
        _userManager = userManager;
        PluginRuntime.Initialize(libraryManager, userManager, itemRepository, playlistManager, providerManager, userDataManager, logManager);
    }

    public void Run() => _userManager.UserCreated += OnUserCreated;

    public void Dispose() => _userManager.UserCreated -= OnUserCreated;

    private static void OnUserCreated(object? sender, GenericEventArgs<User> e)
    {
        try
        {
            var userId = e.Argument.Id.ToString("N");
            PluginRuntime.AutoSharing?.OnUserCreated(userId);
        }
        catch (Exception ex)
        {
            try { PluginRuntime.Log?.Error(LogFormat.FilePrefix + "erreur dans le gestionnaire UserCreated", ex); } catch { /* jamais d'exception vers Emby */ }
        }
    }
}
