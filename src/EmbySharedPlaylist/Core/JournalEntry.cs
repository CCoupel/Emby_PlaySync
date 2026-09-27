namespace EmbySharedPlaylist.Core;

/// <summary>
/// Événement du journal (<see cref="EventJournal"/>). Le moteur n'utilise que <see cref="Ts"/>, <see cref="Kind"/>,
/// <see cref="UserId"/>, <see cref="ItemId"/>, <see cref="PlaylistId"/> et <see cref="Detail"/> (voir <c>Diagnostics/Journal</c>,
/// contracts/http-endpoints.md) ; les autres champs restent disponibles pour la propagation du flag lu (#21).
/// </summary>
public sealed class JournalEntry
{
    public string Ts { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string? ItemId { get; set; }
    public string? PlaylistId { get; set; }
    /// <summary>Identifiant d'entrée de playlist (PlaylistItemId), pour les événements PlaylistItems*.</summary>
    public string? EntryId { get; set; }
    public bool? Played { get; set; }
    /// <summary>PlaybackPositionTicks des données utilisateur (UserDataSaved).</summary>
    public long? PositionTicks { get; set; }
    /// <summary>LastPlayedDate des données utilisateur (ISO-8601), si présente.</summary>
    public string? LastPlayedDate { get; set; }
    /// <summary><c>SaveReason</c> pour UserDataSaved ; <c>UpdateReason</c> pour ItemUpdated.</summary>
    public string? SaveReason { get; set; }
    public bool PluginWrite { get; set; }
    /// <summary>Texte libre (ids et compteurs uniquement), interprété selon <see cref="Kind"/> (voir contracts/http-endpoints.md).</summary>
    public string? Detail { get; set; }
}
