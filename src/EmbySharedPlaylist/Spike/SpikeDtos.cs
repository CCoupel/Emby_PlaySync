using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace EmbySharedPlaylist.Spike;

// Contrats : contracts/http-endpoints.md (section Spike, temporaire).
// JSON en PascalCase : c'est la casse réelle du sérialiseur d'Emby (il ignore [JsonPropertyName]).
// Corps de requête : voir le contrat pour la casse acceptée.

public class EntryDto
{
    public string PlaylistItemId { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
}

public class ShareDto
{
    public string UserId { get; set; } = string.Empty;
    public string ShareLevel { get; set; } = string.Empty;
}

[Route("/SharedPlaylist/Spike/Setup", "POST")]
[Authenticated(Roles = "Admin")]
public class SpikeSetup : IReturn<SpikeSetupResult>
{
    public string OwnerUserId { get; set; } = string.Empty;
    public List<string> MemberUserIds { get; set; } = new();
    public List<string> ItemIds { get; set; } = new();
    public string? Name { get; set; }
}

public class SpikeSetupResult
{
    public string PlaylistId { get; set; } = string.Empty;
    public List<ShareDto> Shares { get; set; } = new();
    public List<EntryDto> Entries { get; set; } = new();
}

[Route("/SharedPlaylist/Spike/Playlists", "GET")]
[Authenticated(Roles = "Admin")]
public class SpikePlaylists : IReturn<List<SpikePlaylistDto>>
{
    public string UserId { get; set; } = string.Empty;
}

public class SpikePlaylistDto
{
    public string PlaylistId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? OwnerUserId { get; set; }
    public string ShareLevel { get; set; } = string.Empty;
    public bool CanManageAccess { get; set; }
    public bool CanLeaveSharedContent { get; set; }
    /// <summary>Playlist publique (visible de tous en lecture) : explique une visibilité sans ligne de partage.</summary>
    public bool IsPublic { get; set; }
    public int EntryCount { get; set; }
    public List<EntryDto> Entries { get; set; } = new();
}

[Route("/SharedPlaylist/Spike/RemoveItem", "POST")]
[Authenticated(Roles = "Admin")]
public class SpikeRemoveItem : IReturn<SpikeRemoveItemResult>
{
    public string PlaylistId { get; set; } = string.Empty;
    public List<string> PlaylistItemIds { get; set; } = new();
}

public class SpikeRemoveItemResult
{
    public bool Removed { get; set; }
    public List<EntryDto> EntriesAfter { get; set; } = new();
}

[Route("/SharedPlaylist/Spike/MarkPlayed", "POST")]
[Authenticated(Roles = "Admin")]
public class SpikeMarkPlayed : IReturn<SpikeMarkPlayedResult>
{
    public string UserId { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public bool Played { get; set; } = true;
    public bool AsPlugin { get; set; }
}

public class SpikeMarkPlayedResult
{
    public bool Saved { get; set; }
    public bool PlayedAfter { get; set; }
}

[Route("/SharedPlaylist/Spike/Events", "GET")]
[Authenticated(Roles = "Admin")]
public class SpikeEvents : IReturn<List<SpikeEventDto>>
{
    public bool Clear { get; set; }
    /// <summary>Filtre optionnel : SaveReason(s) séparés par des virgules (ex. PlaybackFinished,PlaybackProgress).</summary>
    public string? SaveReason { get; set; }
    /// <summary>Filtre optionnel : Kind(s) séparés par des virgules (ex. Probe).</summary>
    public string? Kind { get; set; }
}

public class SpikeEventDto
{
    public string Ts { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string? ItemId { get; set; }
    public string? PlaylistId { get; set; }
    public string? EntryId { get; set; }
    public bool? Played { get; set; }
    public long? PositionTicks { get; set; }
    public string? LastPlayedDate { get; set; }
    public string? SaveReason { get; set; }
    public bool PluginWrite { get; set; }
    /// <summary>Kind Probe : <c>scenario=Pn durationMs=… lockWaitMs=… echoes=… outcome=OK|KO …</c> (ids et compteurs).</summary>
    public string? Detail { get; set; }
}

[Route("/SharedPlaylist/Spike/SetPosition", "POST")]
[Authenticated(Roles = "Admin")]
public class SpikeSetPosition : IReturn<SpikeSetPositionResult>
{
    public string UserId { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public long PositionTicks { get; set; }
}

public class SpikeSetPositionResult
{
    public bool Saved { get; set; }
    public long PositionTicks { get; set; }
    public bool Played { get; set; }
    public int PlayCount { get; set; }
    public string? LastPlayedDate { get; set; }
}

[Route("/SharedPlaylist/Spike/Shares", "GET")]
[Authenticated(Roles = "Admin")]
public class SpikeShares : IReturn<SpikeSharesResult>
{
    public string PlaylistId { get; set; } = string.Empty;
}

public class SpikeSharesResult
{
    public string PlaylistId { get; set; } = string.Empty;
    public string? OwnerUserId { get; set; }
    public List<ShareDto> Shares { get; set; } = new();
}

[Route("/SharedPlaylist/Spike/Tags", "GET")]
[Route("/SharedPlaylist/Spike/Tags", "POST")]
[Authenticated(Roles = "Admin")]
public class SpikeTags : IReturn<SpikeTagsResult>
{
    public string PlaylistId { get; set; } = string.Empty;
    public List<string>? AddTags { get; set; }
    public List<string>? RemoveTags { get; set; }
    public string? Overview { get; set; }
}

public class SpikeTagsResult
{
    public string PlaylistId { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public string? Overview { get; set; }
}

[Route("/SharedPlaylist/Spike/Policy", "GET")]
[Route("/SharedPlaylist/Spike/Policy", "POST")]
[Authenticated(Roles = "Admin")]
public class SpikePolicy : IReturn<SpikePolicyResult>
{
    public string UserId { get; set; } = string.Empty;
    public bool AllowSharingPersonalItems { get; set; }
}

public class SpikePolicyResult
{
    public string UserId { get; set; } = string.Empty;
    public bool AllowSharingPersonalItems { get; set; }
}
