using System.Text.Json.Serialization;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace EmbySharedPlaylist.Spike;

// Contrats : contracts/http-endpoints.md (section Spike, temporaire). JSON en camelCase.

public class EntryDto
{
    [JsonPropertyName("playlistItemId")] public string PlaylistItemId { get; set; } = string.Empty;
    [JsonPropertyName("itemId")] public string ItemId { get; set; } = string.Empty;
}

public class ShareDto
{
    [JsonPropertyName("userId")] public string UserId { get; set; } = string.Empty;
    [JsonPropertyName("shareLevel")] public string ShareLevel { get; set; } = string.Empty;
}

[Route("/SharedPlaylist/Spike/Setup", "POST")]
[Authenticated(Roles = "Admin")]
public class SpikeSetup : IReturn<SpikeSetupResult>
{
    [JsonPropertyName("ownerUserId")] public string OwnerUserId { get; set; } = string.Empty;
    [JsonPropertyName("memberUserIds")] public List<string> MemberUserIds { get; set; } = new();
    [JsonPropertyName("itemIds")] public List<string> ItemIds { get; set; } = new();
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public class SpikeSetupResult
{
    [JsonPropertyName("playlistId")] public string PlaylistId { get; set; } = string.Empty;
    [JsonPropertyName("shares")] public List<ShareDto> Shares { get; set; } = new();
    [JsonPropertyName("entries")] public List<EntryDto> Entries { get; set; } = new();
}

[Route("/SharedPlaylist/Spike/Playlists", "GET")]
[Authenticated(Roles = "Admin")]
public class SpikePlaylists : IReturn<List<SpikePlaylistDto>>
{
    public string UserId { get; set; } = string.Empty;
}

public class SpikePlaylistDto
{
    [JsonPropertyName("playlistId")] public string PlaylistId { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("ownerUserId")] public string? OwnerUserId { get; set; }
    [JsonPropertyName("shareLevel")] public string ShareLevel { get; set; } = string.Empty;
    [JsonPropertyName("canManageAccess")] public bool CanManageAccess { get; set; }
    [JsonPropertyName("canLeaveSharedContent")] public bool CanLeaveSharedContent { get; set; }
    [JsonPropertyName("entryCount")] public int EntryCount { get; set; }
    [JsonPropertyName("entries")] public List<EntryDto> Entries { get; set; } = new();
}

[Route("/SharedPlaylist/Spike/RemoveItem", "POST")]
[Authenticated(Roles = "Admin")]
public class SpikeRemoveItem : IReturn<SpikeRemoveItemResult>
{
    [JsonPropertyName("playlistId")] public string PlaylistId { get; set; } = string.Empty;
    [JsonPropertyName("playlistItemIds")] public List<string> PlaylistItemIds { get; set; } = new();
}

public class SpikeRemoveItemResult
{
    [JsonPropertyName("removed")] public bool Removed { get; set; }
    [JsonPropertyName("entriesAfter")] public List<EntryDto> EntriesAfter { get; set; } = new();
}

[Route("/SharedPlaylist/Spike/MarkPlayed", "POST")]
[Authenticated(Roles = "Admin")]
public class SpikeMarkPlayed : IReturn<SpikeMarkPlayedResult>
{
    [JsonPropertyName("userId")] public string UserId { get; set; } = string.Empty;
    [JsonPropertyName("itemId")] public string ItemId { get; set; } = string.Empty;
    [JsonPropertyName("played")] public bool Played { get; set; } = true;
    [JsonPropertyName("asPlugin")] public bool AsPlugin { get; set; }
}

public class SpikeMarkPlayedResult
{
    [JsonPropertyName("saved")] public bool Saved { get; set; }
    [JsonPropertyName("playedAfter")] public bool PlayedAfter { get; set; }
}

[Route("/SharedPlaylist/Spike/Events", "GET")]
[Authenticated(Roles = "Admin")]
public class SpikeEvents : IReturn<List<SpikeEventDto>>
{
    public bool Clear { get; set; }
    /// <summary>Filtre optionnel : SaveReason(s) séparés par des virgules (ex. PlaybackFinished,PlaybackProgress).</summary>
    public string? SaveReason { get; set; }
}

public class SpikeEventDto
{
    [JsonPropertyName("ts")] public string Ts { get; set; } = string.Empty;
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
    [JsonPropertyName("userId")] public string? UserId { get; set; }
    [JsonPropertyName("itemId")] public string? ItemId { get; set; }
    [JsonPropertyName("playlistId")] public string? PlaylistId { get; set; }
    [JsonPropertyName("entryId")] public string? EntryId { get; set; }
    [JsonPropertyName("played")] public bool? Played { get; set; }
    [JsonPropertyName("positionTicks")] public long? PositionTicks { get; set; }
    [JsonPropertyName("lastPlayedDate")] public string? LastPlayedDate { get; set; }
    [JsonPropertyName("saveReason")] public string? SaveReason { get; set; }
    [JsonPropertyName("pluginWrite")] public bool PluginWrite { get; set; }
}

[Route("/SharedPlaylist/Spike/SetPosition", "POST")]
[Authenticated(Roles = "Admin")]
public class SpikeSetPosition : IReturn<SpikeSetPositionResult>
{
    [JsonPropertyName("userId")] public string UserId { get; set; } = string.Empty;
    [JsonPropertyName("itemId")] public string ItemId { get; set; } = string.Empty;
    [JsonPropertyName("positionTicks")] public long PositionTicks { get; set; }
}

public class SpikeSetPositionResult
{
    [JsonPropertyName("saved")] public bool Saved { get; set; }
    [JsonPropertyName("positionTicks")] public long PositionTicks { get; set; }
    [JsonPropertyName("played")] public bool Played { get; set; }
    [JsonPropertyName("playCount")] public int PlayCount { get; set; }
    [JsonPropertyName("lastPlayedDate")] public string? LastPlayedDate { get; set; }
}

[Route("/SharedPlaylist/Spike/Shares", "GET")]
[Authenticated(Roles = "Admin")]
public class SpikeShares : IReturn<SpikeSharesResult>
{
    public string PlaylistId { get; set; } = string.Empty;
}

public class SpikeSharesResult
{
    [JsonPropertyName("playlistId")] public string PlaylistId { get; set; } = string.Empty;
    [JsonPropertyName("ownerUserId")] public string? OwnerUserId { get; set; }
    [JsonPropertyName("shares")] public List<ShareDto> Shares { get; set; } = new();
}

[Route("/SharedPlaylist/Spike/Tags", "GET")]
[Route("/SharedPlaylist/Spike/Tags", "POST")]
[Authenticated(Roles = "Admin")]
public class SpikeTags : IReturn<SpikeTagsResult>
{
    [JsonPropertyName("playlistId")] public string PlaylistId { get; set; } = string.Empty;
    [JsonPropertyName("addTags")] public List<string>? AddTags { get; set; }
    [JsonPropertyName("removeTags")] public List<string>? RemoveTags { get; set; }
    [JsonPropertyName("overview")] public string? Overview { get; set; }
}

public class SpikeTagsResult
{
    [JsonPropertyName("playlistId")] public string PlaylistId { get; set; } = string.Empty;
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    [JsonPropertyName("overview")] public string? Overview { get; set; }
}

[Route("/SharedPlaylist/Spike/Policy", "GET")]
[Route("/SharedPlaylist/Spike/Policy", "POST")]
[Authenticated(Roles = "Admin")]
public class SpikePolicy : IReturn<SpikePolicyResult>
{
    [JsonPropertyName("userId")] public string UserId { get; set; } = string.Empty;
    [JsonPropertyName("allowSharingPersonalItems")] public bool AllowSharingPersonalItems { get; set; }
}

public class SpikePolicyResult
{
    [JsonPropertyName("userId")] public string UserId { get; set; } = string.Empty;
    [JsonPropertyName("allowSharingPersonalItems")] public bool AllowSharingPersonalItems { get; set; }
}
