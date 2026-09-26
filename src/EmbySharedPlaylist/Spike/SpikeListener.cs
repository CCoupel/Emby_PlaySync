using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace EmbySharedPlaylist.Spike;

/// <summary>
/// Écoute UserDataSaved, PlaylistItemsAdded/Removed/Moved et ItemUpdated (playlists) et alimente le journal du spike.
/// Les abonnements sont permanents ; l'enregistrement n'a lieu que si EnableSpikeEndpoints est vrai.
/// </summary>
public sealed class SpikeListener : IServerEntryPoint
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IPlaylistManager _playlistManager;
    private readonly ILogger _logger;

    public SpikeListener(ILibraryManager libraryManager, IUserDataManager userDataManager,
        IPlaylistManager playlistManager, ILogManager logManager)
    {
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _playlistManager = playlistManager;
        _logger = logManager.GetLogger("EmbySharedPlaylist");
    }

    public void Run()
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;
        _playlistManager.PlaylistItemsAdded += OnItemsAdded;
        _playlistManager.PlaylistItemsRemoved += OnItemsRemoved;
        _playlistManager.PlaylistItemsMoved += OnItemsMoved;
        _libraryManager.ItemUpdated += OnItemUpdated;
        _logger.Info("EmbySharedPlaylist : écouteurs du spike enregistrés");
    }

    public void Dispose()
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        _playlistManager.PlaylistItemsAdded -= OnItemsAdded;
        _playlistManager.PlaylistItemsRemoved -= OnItemsRemoved;
        _playlistManager.PlaylistItemsMoved -= OnItemsMoved;
        _libraryManager.ItemUpdated -= OnItemUpdated;
    }

    private static bool Enabled => Plugin.Instance?.Configuration.EnableSpikeEndpoints == true;

    private static JournalEntry NewEntry(string kind) => new()
    {
        Ts = DateTime.UtcNow.ToString("o"),
        Kind = kind
    };

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        if (!Enabled) return;
        try
        {
            var played = e.UserData?.Played;
            // Tout est journalisé (dont PlaybackProgress, périodique) pour observer arrêt/pause/progression ;
            // filtrer par saveReason à la lecture et vider (clear) régulièrement : le journal est borné à 500.
            var entry = NewEntry("UserDataSaved");
            entry.UserId = e.User.Id.ToString("N");
            entry.ItemId = e.Item.InternalId.ToString();
            entry.Played = played;
            entry.PositionTicks = e.UserData?.PlaybackPositionTicks;
            entry.LastPlayedDate = e.UserData?.LastPlayedDate?.ToString("o");
            entry.SaveReason = e.SaveReason.ToString();
            entry.PluginWrite = SpikeRuntime.Tracker.TryConsume(e.User.InternalId, e.Item.InternalId);
            SpikeRuntime.Journal.Add(entry);
        }
        catch (Exception ex)
        {
            _logger.ErrorException("EmbySharedPlaylist : erreur dans UserDataSaved", ex);
        }
    }

    private void OnItemsAdded(object? sender, PlaylistItemsAddedEventArgs e)
    {
        if (!Enabled) return;
        try
        {
            foreach (var li in e.ListItems ?? Array.Empty<ListItem>())
            {
                var entry = NewEntry("PlaylistItemsAdded");
                entry.PlaylistId = e.Playlist.InternalId.ToString();
                entry.ItemId = li.ListItemId.ToString();
                entry.EntryId = li.ListItemEntryId.ToString();
                SpikeRuntime.Journal.Add(entry);
            }
        }
        catch (Exception ex)
        {
            _logger.ErrorException("EmbySharedPlaylist : erreur dans PlaylistItemsAdded", ex);
        }
    }

    private void OnItemsRemoved(object? sender, PlaylistItemsRemovedEventArgs e) =>
        AddEntries("PlaylistItemsRemoved", e.Playlist, e.ListItemEntryIds);

    private void OnItemsMoved(object? sender, PlaylistItemsMovedEventArgs e) =>
        AddEntries("PlaylistItemsMoved", e.Playlist, e.ListItemEntryIds);

    private void AddEntries(string kind, Playlist playlist, long[]? entryIds)
    {
        if (!Enabled) return;
        try
        {
            foreach (var id in entryIds ?? Array.Empty<long>())
            {
                var entry = NewEntry(kind);
                entry.PlaylistId = playlist.InternalId.ToString();
                entry.EntryId = id.ToString();
                SpikeRuntime.Journal.Add(entry);
            }
        }
        catch (Exception ex)
        {
            _logger.ErrorException("EmbySharedPlaylist : erreur dans " + kind, ex);
        }
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        if (!Enabled || e.Item is not Playlist playlist) return;
        try
        {
            var entry = NewEntry("ItemUpdated");
            entry.PlaylistId = playlist.InternalId.ToString();
            entry.SaveReason = e.UpdateReason.ToString();
            SpikeRuntime.Journal.Add(entry);
        }
        catch (Exception ex)
        {
            _logger.ErrorException("EmbySharedPlaylist : erreur dans ItemUpdated", ex);
        }
    }
}
