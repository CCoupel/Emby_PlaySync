using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace EmbySharedPlaylist.Spike;

/// <summary>
/// Endpoints de diagnostic (temporaires, v0.1.0) — voir contracts/http-endpoints.md.
/// Admin uniquement (attribut sur chaque requête) ; 404 si EnableSpikeEndpoints est faux.
/// Garde-fous : les écritures ne touchent que les comptes test_* et les playlists SPIKE*.
/// </summary>
public class SpikeService : IService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IPlaylistManager _playlistManager;
    private readonly IItemRepository _itemRepository;
    private readonly ILogger _logger;

    public SpikeService(ILibraryManager libraryManager, IUserManager userManager, IUserDataManager userDataManager,
        IPlaylistManager playlistManager, IItemRepository itemRepository, ILogManager logManager)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _playlistManager = playlistManager;
        _itemRepository = itemRepository;
        _logger = logManager.GetLogger("EmbySharedPlaylist");
    }

    // ---- Setup -------------------------------------------------------------------------------

    public Task<object> Post(SpikeSetup request) => RunAsync(nameof(SpikeSetup), async () =>
    {
        var owner = RequireTestUser(request.OwnerUserId);
        var members = request.MemberUserIds.Select(RequireTestUser).ToList();
        var itemIds = request.ItemIds.Select(ParseId).ToArray();

        var created = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
        {
            Name = SpikeRules.EnsureSpikeName(request.Name),
            ItemIdList = itemIds,
            MediaType = "Video",
            User = owner
        }).ConfigureAwait(false);

        var playlist = RequirePlaylist(created.Id);
        if (members.Count > 0)
        {
            _libraryManager.SaveUserItemShares(members.Select(m => new UserItemShare
            {
                UserId = m.InternalId,
                ItemId = playlist.InternalId,
                ShareLevel = UserItemShareLevel.Write
            }).ToArray());
        }

        _logger.Info("EmbySharedPlaylist spike : playlist {0} créée pour {1}, {2} membre(s)",
            playlist.InternalId, owner.Name, members.Count);

        return (object)new SpikeSetupResult
        {
            PlaylistId = playlist.InternalId.ToString(),
            Shares = GetShares(playlist),
            Entries = GetEntries(playlist, null)
        };
    });

    // ---- Playlists ---------------------------------------------------------------------------

    public object Get(SpikePlaylists request) => Run(() =>
    {
        var user = RequireUser(request.UserId);
        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { "Playlist" },
            Recursive = true
        };
        var result = new List<SpikePlaylistDto>();
        foreach (var item in _libraryManager.GetItemList(query))
        {
            if (item is not Playlist playlist) continue;
            var level = playlist.GetShareLevel(user, CancellationToken.None);
            var entries = GetEntries(playlist, user);
            result.Add(new SpikePlaylistDto
            {
                PlaylistId = playlist.InternalId.ToString(),
                Name = playlist.Name,
                OwnerUserId = GetOwner(GetShares(playlist)),
                ShareLevel = level.ToString(),
                CanManageAccess = playlist.CanManageAccess(user, level),
                CanLeaveSharedContent = playlist.CanLeaveSharedContent(user, level),
                EntryCount = entries.Count,
                Entries = entries
            });
        }
        return result;
    });

    // ---- RemoveItem --------------------------------------------------------------------------

    public Task<object> Post(SpikeRemoveItem request) => RunAsync(nameof(SpikeRemoveItem), async () =>
    {
        var playlist = RequireSpikePlaylist(request.PlaylistId);
        var entryIds = request.PlaylistItemIds.Select(ParseId).ToArray();
        await _playlistManager.RemoveFromPlaylist(playlist, entryIds).ConfigureAwait(false);
        return (object)new SpikeRemoveItemResult { Removed = true, EntriesAfter = GetEntries(playlist, null) };
    });

    // ---- MarkPlayed --------------------------------------------------------------------------

    public object Post(SpikeMarkPlayed request) => Run(() =>
    {
        var user = RequireTestUser(request.UserId);
        var item = RequireItem(request.ItemId);
        var data = _userDataManager.GetUserData(user, item);
        data.Played = request.Played;
        if (request.Played)
        {
            if (data.PlayCount < 1) data.PlayCount = 1;
            data.LastPlayedDate = DateTimeOffset.UtcNow;
        }
        if (request.AsPlugin) SpikeRuntime.Tracker.Register(user.InternalId, item.InternalId);
        _userDataManager.SaveUserData(user, item, data, UserDataSaveReason.TogglePlayed, CancellationToken.None);
        return new SpikeMarkPlayedResult
        {
            Saved = true,
            PlayedAfter = _userDataManager.GetUserData(user, item).Played
        };
    });

    // ---- Events ------------------------------------------------------------------------------

    public object Get(SpikeEvents request) => Run(() =>
        SpikeRuntime.Journal.Snapshot(request.Clear).Select(e => new SpikeEventDto
        {
            Ts = e.Ts, Kind = e.Kind, UserId = e.UserId, ItemId = e.ItemId, PlaylistId = e.PlaylistId,
            EntryId = e.EntryId, Played = e.Played, SaveReason = e.SaveReason, PluginWrite = e.PluginWrite
        }).ToList());

    // ---- Shares ------------------------------------------------------------------------------

    public object Get(SpikeShares request) => Run(() =>
    {
        var playlist = RequirePlaylist(request.PlaylistId);
        var shares = GetShares(playlist);
        return new SpikeSharesResult
        {
            PlaylistId = playlist.InternalId.ToString(),
            OwnerUserId = GetOwner(shares),
            Shares = shares
        };
    });

    // ---- Tags --------------------------------------------------------------------------------

    public object Get(SpikeTags request) => Run(() => TagsResult(RequirePlaylist(request.PlaylistId)));

    public object Post(SpikeTags request) => Run(() =>
    {
        var playlist = RequireSpikePlaylist(request.PlaylistId);
        var tags = SpikeRules.ApplyTagChanges(playlist.Tags, request.AddTags, request.RemoveTags);
        playlist.SetTags(tags);
        if (request.Overview != null) playlist.Overview = request.Overview;
        playlist.UpdateToRepository(ItemUpdateType.MetadataEdit);
        return TagsResult(playlist);
    });

    private static SpikeTagsResult TagsResult(Playlist playlist) => new()
    {
        PlaylistId = playlist.InternalId.ToString(),
        Tags = (playlist.Tags ?? Array.Empty<string>()).ToList(),
        Overview = playlist.Overview
    };

    // ---- Policy ------------------------------------------------------------------------------

    public object Get(SpikePolicy request) => Run(() =>
    {
        var user = RequireUser(request.UserId);
        return new SpikePolicyResult
        {
            UserId = user.Id.ToString("N"),
            AllowSharingPersonalItems = _userManager.GetUserPolicy(user).AllowSharingPersonalItems
        };
    });

    public object Post(SpikePolicy request) => Run(() =>
    {
        var user = RequireTestUser(request.UserId);
        // Politique COMPLÈTE relue, un seul champ modifié, puis réécrite.
        var policy = _userManager.GetUserPolicy(user);
        policy.AllowSharingPersonalItems = request.AllowSharingPersonalItems;
        _userManager.UpdateUserPolicy(user.InternalId, policy);
        _logger.Info("EmbySharedPlaylist spike : AllowSharingPersonalItems={0} pour {1}",
            request.AllowSharingPersonalItems, user.Name);
        return new SpikePolicyResult
        {
            UserId = user.Id.ToString("N"),
            AllowSharingPersonalItems = _userManager.GetUserPolicy(user).AllowSharingPersonalItems
        };
    });

    // ---- Aides -------------------------------------------------------------------------------

    private object Run(Func<object> action)
    {
        EnsureEnabled();
        try { return action(); }
        catch (Exception ex) when (!IsHttpMappedException(ex)) { throw Wrap(ex); }
    }

    private async Task<object> RunAsync(string operation, Func<Task<object>> action)
    {
        EnsureEnabled();
        try { return await action().ConfigureAwait(false); }
        catch (Exception ex) when (!IsHttpMappedException(ex)) { _logger.ErrorException("EmbySharedPlaylist spike : " + operation, ex); throw Wrap(ex); }
    }

    private static void EnsureEnabled()
    {
        if (Plugin.Instance?.Configuration.EnableSpikeEndpoints != true)
            throw new ResourceNotFoundException("Spike endpoints disabled");
    }

    private static bool IsHttpMappedException(Exception ex) =>
        ex is ArgumentException or ResourceNotFoundException or MediaBrowser.Controller.Net.SecurityException;

    /// <summary>500 : le corps porte « Type : message » (jamais de secret dans ces messages).</summary>
    private static Exception Wrap(Exception ex) => new InvalidOperationException($"error: {ex.GetType().Name}: {ex.Message}");

    private static long ParseId(string? id)
    {
        if (!long.TryParse(id, out var value)) throw new ArgumentException("Identifiant invalide : " + id);
        return value;
    }

    private User RequireUser(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("userId requis");
        return _userManager.GetUserById(id) ?? throw new ResourceNotFoundException("Utilisateur introuvable : " + id);
    }

    private User RequireTestUser(string? id)
    {
        var user = RequireUser(id);
        if (!SpikeRules.IsTestUser(user.Name))
            throw new ArgumentException("Le spike ne modifie que les comptes test_* : refusé pour " + user.Name);
        return user;
    }

    private BaseItem RequireItem(string? id) =>
        _libraryManager.GetItemById(ParseId(id)) ?? throw new ResourceNotFoundException("Item introuvable : " + id);

    private Playlist RequirePlaylist(string? id) =>
        RequireItem(id) as Playlist ?? throw new ResourceNotFoundException("Playlist introuvable : " + id);

    private Playlist RequireSpikePlaylist(string? id)
    {
        var playlist = RequirePlaylist(id);
        if (!SpikeRules.IsSpikePlaylist(playlist.Name))
            throw new ArgumentException("Le spike ne modifie que les playlists SPIKE* : refusé pour " + playlist.Name);
        return playlist;
    }

    private static List<EntryDto> GetEntries(Playlist playlist, User? user)
    {
        var query = user != null ? new InternalItemsQuery(user) : new InternalItemsQuery();
        return playlist.GetChildren(query)
            .Select(c => new EntryDto { PlaylistItemId = c.ListItemEntryId.ToString(), ItemId = c.InternalId.ToString() })
            .ToList();
    }

    private List<ShareDto> GetShares(Playlist playlist)
    {
        var shares = _itemRepository.GetUserItemShares(new UserItemShareQuery { ItemIds = new[] { playlist.InternalId } }, CancellationToken.None);
        return shares.Select(s => new ShareDto
        {
            UserId = _userManager.GetGuid(s.UserId).ToString("N"),
            ShareLevel = (s.ShareLevel ?? UserItemShareLevel.None).ToString()
        }).ToList();
    }

    private static string? GetOwner(List<ShareDto> shares) =>
        shares.FirstOrDefault(s => s.ShareLevel is "Manage" or "ManageDelete")?.UserId;
}
