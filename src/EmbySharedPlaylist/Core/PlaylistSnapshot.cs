namespace EmbySharedPlaylist.Core;

/// <summary>Lecture d'une playlist : ids seulement (propriétaire, membres = propriétaire ou ligne de partage ≥ Read), étiquettes, description.</summary>
public sealed record PlaylistSnapshot(
    string Id,
    string? OwnerId,
    IReadOnlyList<string> MemberIds,
    IReadOnlyList<string> Tags,
    string? Overview)
{
    /// <summary>Gérée = propriétaire connu et au moins un membre autre que le propriétaire.</summary>
    public bool IsShared => OwnerId != null && MemberIds.Any(m => !string.Equals(m, OwnerId, StringComparison.Ordinal));
}
