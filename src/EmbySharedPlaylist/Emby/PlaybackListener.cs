using EmbySharedPlaylist.Core;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;

namespace EmbySharedPlaylist.Emby;

/// <summary>
/// Branche le moteur de retrait sur <c>UserDataSaved</c> : chemin rapide en mémoire d'abord (<c>PlayedTransitionTracker</c>,
/// aucun accès base hors transition, ex. <c>PlaybackProgress</c>), puis traitement IMMÉDIAT dans le gestionnaire
/// (<c>ReadRemovalEngine.Handle</c>, verrou par playlist, sans file). Ignoré pendant une écriture du plugin (<see cref="WriteScope"/>)
/// et tant que le moteur est suspendu. Aucune exception n'atteint le pipeline d'Emby.
/// </summary>
public sealed class PlaybackListener : IServerEntryPoint
{
    private readonly IUserDataManager _userDataManager;

    public PlaybackListener(IUserDataManager userDataManager, ILibraryManager libraryManager, IUserManager userManager,
        IItemRepository itemRepository, IPlaylistManager playlistManager, ILogManager logManager)
    {
        _userDataManager = userDataManager;
        PluginRuntime.Initialize(libraryManager, userManager, itemRepository, playlistManager, logManager);
    }

    public void Run() => _userDataManager.UserDataSaved += OnUserDataSaved;

    public void Dispose() => _userDataManager.UserDataSaved -= OnUserDataSaved;

    private static void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        try
        {
            // Garde suspension/WriteScope → tracker (mémoire) → moteur : logique pure testée dans PlaybackEventProcessor.
            PluginRuntime.PlaybackProcessor?.Process(e.User.Id.ToString("N"), e.Item.InternalId.ToString(),
                e.SaveReason.ToString(), e.UserData?.Played ?? false);
        }
        catch (Exception ex)
        {
            try { PluginRuntime.Log?.Error(LogFormat.FilePrefix + "erreur dans le gestionnaire UserDataSaved", ex); } catch { /* jamais d'exception vers Emby */ }
        }
    }
}
