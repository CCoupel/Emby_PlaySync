using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Marker;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace EmbySharedPlaylist.Emby;

/// <summary>
/// Adaptateur du port <see cref="IPlaylistGateway"/> sur le SDK Emby : uniquement des appels internes
/// (<c>GetUserItemShares</c>, <c>RemoveFromPlaylist</c>, <c>SetTags</c> + <c>UpdateToRepository</c>), jamais de SQL.
/// Les identifiants d'utilisateurs sortent en GUID hexadécimal sans tirets, ceux d'items en entier interne (chaîne).
/// Les écritures se font dans un <see cref="WriteScope"/> ; les lectures sont fraîches (aucun cache).
/// </summary>
public sealed class EmbyPlaylistGateway : IPlaylistGateway
{
    private const int CallTimeoutMs = 5000;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IItemRepository _itemRepository;
    private readonly IPlaylistManager _playlistManager;
    private readonly PlaylistEntryReader _entries;
    private readonly Func<bool> _isSuspended;

    /// <summary>Verrou d'écriture unique de la passerelle : la passe planifiée et les gestionnaires peuvent se croiser.</summary>
    private readonly object _writeGate = new();

    /// <param name="isSuspended">Vrai = le moteur est suspendu (sonde U11 active) : aucune écriture, quoi que demande l'appelant (dernier garde-fou).</param>
    public EmbyPlaylistGateway(ILibraryManager libraryManager, IUserManager userManager, IItemRepository itemRepository, IPlaylistManager playlistManager,
        Func<bool>? isSuspended = null)
    {
        _entries = new PlaylistEntryReader(libraryManager, userManager, itemRepository);
        _isSuspended = isSuspended ?? (() => false);
        _libraryManager = libraryManager;
        _userManager = userManager;
        _itemRepository = itemRepository;
        _playlistManager = playlistManager;
    }

    public IReadOnlyList<PlaylistSnapshot> ListSharedPlaylists()
    {
        var playlists = AllPlaylists();
        if (playlists.Length == 0) return Array.Empty<PlaylistSnapshot>();

        var rows = _itemRepository
            .GetUserItemShares(new UserItemShareQuery { ItemIds = playlists.Select(p => p.InternalId).ToArray() }, CancellationToken.None)
            .ToLookup(r => r.ItemId);

        var result = new List<PlaylistSnapshot>();
        foreach (var playlist in playlists)
        {
            var snapshot = Snapshot(playlist, rows[playlist.InternalId]);
            if (snapshot.IsShared) result.Add(snapshot);
        }
        return result;
    }

    public IReadOnlyList<PlaylistSnapshot> ListSharedPlaylistsOfUserContaining(string userId, string itemId)
    {
        if (!long.TryParse(itemId, out var item)) return Array.Empty<PlaylistSnapshot>();
        User? member = null;
        try { member = _userManager.GetUserById(userId); } catch { /* id invalide : lecture sans utilisateur préféré */ }
        var result = new List<PlaylistSnapshot>();
        foreach (var snapshot in ListSharedPlaylists())
        {
            if (!snapshot.MemberIds.Contains(userId, StringComparer.Ordinal)) continue;
            if (long.TryParse(snapshot.Id, out var pid) && _libraryManager.GetItemById(pid) is Playlist playlist
                && ContainsItem(_entries.Read(playlist, member), item))
                result.Add(snapshot);
        }
        return result;
    }

    public PlaylistSnapshot? Get(string playlistId)
    {
        var playlist = FindPlaylist(playlistId);
        if (playlist == null) return null;
        var rows = _itemRepository.GetUserItemShares(new UserItemShareQuery { ItemIds = new[] { playlist.InternalId } }, CancellationToken.None);
        return Snapshot(playlist, rows);
    }

    public bool RemoveOneEntry(string playlistId, string itemId)
    {
        if (_isSuspended()) return false;
        var playlist = FindPlaylist(playlistId);
        if (playlist == null || !long.TryParse(itemId, out var item)) return false;
        // Résolution par ItemId à l'instant : les identifiants d'entrée ne sont pas stables.
        var read = _entries.Read(playlist);
        var entry = read.Entries.FirstOrDefault(c => c.ItemId == item);
        if (entry == null)
        {
            // Repli (non vérifié en réel) : si Emby ne fournit AUCUN identifiant d'entrée (ListItemEntryId = 0) pour ce média,
            // retrait par ItemId via l'API interne du dépôt (retire d'un coup tous les doublons ; l'appel suivant ne trouve plus rien).
            if (!read.WithoutEntryId.Contains(item)) return false;
            using (WriteScope.Enter()) _itemRepository.RemoveListItemsByItemIds(playlist.InternalId, new[] { item });
            return true;
        }

        using (WriteScope.Enter())
        {
            var task = _playlistManager.RemoveFromPlaylist(playlist, new[] { entry.EntryId });
            if (!task.Wait(CallTimeoutMs)) throw new TimeoutException("RemoveFromPlaylist > 5 s");
            task.GetAwaiter().GetResult();
        }
        return true;
    }

    public ApplyResult ApplyDefaults(string playlistId, IReadOnlyList<MarkerFamily> familiesToPose, string? overviewIfEmpty)
    {
        if (_isSuspended()) return new ApplyResult();
        lock (_writeGate)
        {
            // Lecture fraîche PUIS écriture, dans la même section critique : on ne pose que ce qui manque à cet instant.
            var playlist = FindPlaylist(playlistId);
            if (playlist == null) return new ApplyResult();

            var tags = (playlist.Tags ?? Array.Empty<string>()).ToList();
            var posed = new List<MarkerFamily>();
            foreach (var family in familiesToPose)
            {
                if (MarkerEvaluator.Evaluate(tags, family) != MarkerState.None) continue;
                tags.Add(MarkerEvaluator.NonTag(family));
                posed.Add(family);
            }

            var overviewWritten = false;
            if (overviewIfEmpty != null && string.IsNullOrWhiteSpace(playlist.Overview))
            {
                playlist.Overview = overviewIfEmpty;
                overviewWritten = true;
            }

            if (posed.Count == 0 && !overviewWritten) return new ApplyResult();

            using (WriteScope.Enter())
            {
                if (posed.Count > 0) playlist.SetTags(tags); // uniquement des ajouts : la liste relue + les nouvelles étiquettes
                playlist.UpdateToRepository(ItemUpdateType.MetadataEdit);
            }
            return new ApplyResult(posed, overviewWritten);
        }
    }

    // ---- Aides -------------------------------------------------------------------------------------------

    private static bool ContainsItem(EntryReadResult read, long item) =>
        read.Entries.Any(c => c.ItemId == item) || read.WithoutEntryId.Contains(item);

    private Playlist[] AllPlaylists() =>
        _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { "Playlist" }, Recursive = true })
            .OfType<Playlist>().ToArray();

    private Playlist? FindPlaylist(string playlistId) =>
        long.TryParse(playlistId, out var id) ? _libraryManager.GetItemById(id) as Playlist : null;

    private PlaylistSnapshot Snapshot(Playlist playlist, IEnumerable<UserItemShare> rows)
    {
        var classified = ShareClassifier.Classify(rows.Select(r =>
            (_userManager.GetGuid(r.UserId).ToString("N"), r.ShareLevel ?? UserItemShareLevel.None)));
        return new PlaylistSnapshot(playlist.InternalId.ToString(), classified.OwnerId, classified.MemberIds,
            playlist.Tags ?? Array.Empty<string>(), playlist.Overview);
    }
}
