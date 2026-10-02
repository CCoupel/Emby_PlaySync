using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Engine;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Providers;
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
        IItemRepository itemRepository, IPlaylistManager playlistManager, IProviderManager providerManager, ILogManager logManager)
    {
        _userDataManager = userDataManager;
        PluginRuntime.Initialize(libraryManager, userManager, itemRepository, playlistManager, providerManager, userDataManager, logManager);
    }

    public void Run() => _userDataManager.UserDataSaved += OnUserDataSaved;

    public void Dispose() => _userDataManager.UserDataSaved -= OnUserDataSaved;

    private static void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        try
        {
            var userId = e.User.Id.ToString("N");
            var itemId = e.Item.InternalId.ToString();
            // Garde WriteScope/PluginWriteTracker → tracker de transition (mémoire) → moteur : logique pure testée dans PlaybackEventProcessor.
            var outcome = PluginRuntime.PlaybackProcessor?.Process(userId, itemId, e.SaveReason.ToString(), e.UserData?.Played ?? false);
            if (outcome == PlaybackEventOutcome.Echo)
            {
                // Au niveau de l'événement (PlaylistId=null), pas de la playlist : l'écriture du plugin est reconnue et consommée.
                var entry = JournalEntries.SkippedEntry(null, null, "echo-consumed");
                entry.UserId = userId;
                entry.ItemId = itemId;
                PluginRuntime.Journal?.Add(entry);
            }
        }
        catch (Exception ex)
        {
            try { PluginRuntime.Log?.Error(LogFormat.FilePrefix + "erreur dans le gestionnaire UserDataSaved", ex); } catch { /* jamais d'exception vers Emby */ }
        }
    }
}
