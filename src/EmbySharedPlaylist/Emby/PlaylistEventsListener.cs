using EmbySharedPlaylist.Core;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;

namespace EmbySharedPlaylist.Emby;

/// <summary>
/// Première détection à l'action (N1) : ajout/retrait d'entrée ou modification d'une playlist NON encore vue → pose immédiate
/// (sous le verrou de la playlist, dans le gestionnaire, avec <see cref="WriteScope"/>). Jamais sur une playlist déjà vue ni pendant
/// une écriture du plugin. Le gestionnaire ne lève jamais : aucune exception n'atteint le pipeline d'Emby.
/// </summary>
public sealed class PlaylistEventsListener : IServerEntryPoint
{
    private readonly ILibraryManager _libraryManager;
    private readonly IPlaylistManager _playlistManager;

    public PlaylistEventsListener(ILibraryManager libraryManager, IPlaylistManager playlistManager, IProviderManager providerManager, IUserManager userManager,
        IItemRepository itemRepository, IUserDataManager userDataManager, ILogManager logManager)
    {
        _libraryManager = libraryManager;
        _playlistManager = playlistManager;
        PluginRuntime.Initialize(libraryManager, userManager, itemRepository, playlistManager, providerManager, userDataManager, logManager);
    }

    public void Run()
    {
        _playlistManager.PlaylistItemsAdded += OnAdded;
        _playlistManager.PlaylistItemsRemoved += OnRemoved;
        _libraryManager.ItemUpdated += OnItemUpdated;
    }

    public void Dispose()
    {
        _playlistManager.PlaylistItemsAdded -= OnAdded;
        _playlistManager.PlaylistItemsRemoved -= OnRemoved;
        _libraryManager.ItemUpdated -= OnItemUpdated;
    }

    private static void Handle(string playlistId)
    {
        try { PluginRuntime.FirstDetection?.OnPlaylistEvent(playlistId); }
        catch { /* jamais d'exception vers Emby (le coordinateur a déjà journalisé) */ }
    }

    private void OnAdded(object? sender, PlaylistItemsAddedEventArgs e) => Handle(e.Playlist.InternalId.ToString());

    private void OnRemoved(object? sender, PlaylistItemsRemovedEventArgs e) => Handle(e.Playlist.InternalId.ToString());

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Playlist playlist) Handle(playlist.InternalId.ToString());
    }
}
