using EmbySharedPlaylist.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace EmbySharedPlaylist.Spike;

/// <summary>
/// Écoute UserDataSaved, PlaylistItemsAdded/Removed/Moved et ItemUpdated (playlists) et alimente le journal du spike.
/// Les abonnements sont permanents. Deux options INDÉPENDANTES : le journal du spike n'est alimenté que si EnableSpikeEndpoints est vrai ;
/// la sonde de réentrance ne dépend que de EnableReentrancyProbe (elle écrit ses résultats dans le journal même si le spike est désactivé).
/// </summary>
public sealed class SpikeListener : IServerEntryPoint
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IPlaylistManager _playlistManager;
    private readonly Log _log;
    private readonly ReentrancyProbe _probe;

    public SpikeListener(ILibraryManager libraryManager, IUserDataManager userDataManager,
        IPlaylistManager playlistManager, IUserManager userManager, IItemRepository itemRepository, ILogManager logManager)
    {
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _playlistManager = playlistManager;
        _log = new Log(logManager.GetLogger("EmbySharedPlaylist"));
        _probe = new ReentrancyProbe(libraryManager, userManager, userDataManager, playlistManager, itemRepository, SpikeRuntime.Locks, _log);
    }

    public void Run()
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;
        _playlistManager.PlaylistItemsAdded += OnItemsAdded;
        _playlistManager.PlaylistItemsRemoved += OnItemsRemoved;
        _playlistManager.PlaylistItemsMoved += OnItemsMoved;
        _libraryManager.ItemUpdated += OnItemUpdated;
        SpikeRuntime.Log = _log;
        _log.Info(LogFormat.Startup(Enabled));
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

    /// <summary>Une ligne dans le journal Emby par événement observé (ids seulement) ; PlaybackProgress en Debug.</summary>
    private void LogEntry(JournalEntry entry)
    {
        var line = SpikeLogFormat.Event(entry);
        if (SpikeLogFormat.IsDebugLevel(entry)) _log.Debug(line);
        else _log.Info(line);
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        // La sonde ne dépend QUE de EnableReentrancyProbe, le journal du spike QUE de EnableSpikeEndpoints : deux options indépendantes.
        var spike = Enabled;
        var probe = ReentrancyProbe.Enabled;
        if (!spike && !probe) return;
        try
        {
            var userId = e.User.Id.ToString("N");
            var itemId = e.Item.InternalId.ToString();
            var pluginWrite = SpikeRuntime.Tracker.TryConsume(e.User.InternalId, e.Item.InternalId);

            if (spike)
            {
                // Tout est journalisé (dont PlaybackProgress, périodique) pour observer arrêt/pause/progression ;
                // filtrer par saveReason à la lecture et vider (clear) régulièrement : le journal est borné à 500.
                var entry = NewEntry("UserDataSaved");
                entry.UserId = userId;
                entry.ItemId = itemId;
                entry.Played = e.UserData?.Played;
                entry.PositionTicks = e.UserData?.PlaybackPositionTicks;
                entry.LastPlayedDate = e.UserData?.LastPlayedDate?.ToString("o");
                entry.SaveReason = e.SaveReason.ToString();
                entry.PluginWrite = pluginWrite;
                SpikeRuntime.Journal.Add(entry);
                LogEntry(entry);
            }

            if (probe)
            {
                _probe.ObserveEcho("UserDataSaved", null, userId, itemId);
                _probe.OnUserDataSaved(e, pluginWrite);
            }
        }
        catch (Exception ex)
        {
            _log.Error("EmbySharedPlaylist : erreur dans UserDataSaved", ex);
        }
    }

    private void OnItemsAdded(object? sender, PlaylistItemsAddedEventArgs e)
    {
        var spike = Enabled;
        var probe = ReentrancyProbe.Enabled;
        if (!spike && !probe) return;
        try
        {
            if (spike)
            {
                foreach (var li in e.ListItems ?? Array.Empty<ListItem>())
                {
                    var entry = NewEntry("PlaylistItemsAdded");
                    entry.PlaylistId = e.Playlist.InternalId.ToString();
                    entry.ItemId = li.ListItemId.ToString();
                    entry.EntryId = li.ListItemEntryId.ToString();
                    SpikeRuntime.Journal.Add(entry);
                    LogEntry(entry);
                }
            }
            if (probe)
            {
                _probe.ObserveEcho("PlaylistItemsAdded", e.Playlist.InternalId.ToString(), null, null);
                _probe.OnPlaylistItemsAdded(e);
            }
        }
        catch (Exception ex)
        {
            _log.Error("EmbySharedPlaylist : erreur dans PlaylistItemsAdded", ex);
        }
    }

    private void OnItemsRemoved(object? sender, PlaylistItemsRemovedEventArgs e) =>
        AddEntries("PlaylistItemsRemoved", e.Playlist, e.ListItemEntryIds);

    private void OnItemsMoved(object? sender, PlaylistItemsMovedEventArgs e) =>
        AddEntries("PlaylistItemsMoved", e.Playlist, e.ListItemEntryIds);

    private void AddEntries(string kind, Playlist playlist, long[]? entryIds)
    {
        var spike = Enabled;
        var probe = ReentrancyProbe.Enabled;
        if (!spike && !probe) return;
        try
        {
            if (probe) _probe.ObserveEcho(kind, playlist.InternalId.ToString(), null, null);
            if (!spike) return;
            foreach (var id in entryIds ?? Array.Empty<long>())
            {
                var entry = NewEntry(kind);
                entry.PlaylistId = playlist.InternalId.ToString();
                entry.EntryId = id.ToString();
                SpikeRuntime.Journal.Add(entry);
                LogEntry(entry);
            }
        }
        catch (Exception ex)
        {
            _log.Error("EmbySharedPlaylist : erreur dans " + kind, ex);
        }
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is not Playlist playlist) return;
        var spike = Enabled;
        var probe = ReentrancyProbe.Enabled;
        if (!spike && !probe) return;
        try
        {
            var playlistId = playlist.InternalId.ToString();
            if (spike)
            {
                var entry = NewEntry("ItemUpdated");
                entry.PlaylistId = playlistId;
                entry.SaveReason = e.UpdateReason.ToString();
                SpikeRuntime.Journal.Add(entry);
                LogEntry(entry);
            }
            if (probe)
            {
                _probe.ObserveEcho("ItemUpdated", playlistId, null, null);
                _probe.OnItemUpdated(e);
            }
        }
        catch (Exception ex)
        {
            _log.Error("EmbySharedPlaylist : erreur dans ItemUpdated", ex);
        }
    }
}
