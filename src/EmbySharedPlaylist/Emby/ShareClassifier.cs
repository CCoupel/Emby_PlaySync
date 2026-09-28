using MediaBrowser.Model.Dto;

namespace EmbySharedPlaylist.Emby;

/// <summary>
/// Classe les lignes de partage d'une playlist (fonction pure). Membre = ligne de partage de niveau ≥ Read (la simple
/// visibilité ne suffit pas). Propriétaire = ligne <c>ManageDelete</c> (constaté en réel : le créateur en porte une). Une playlist
/// est gérée si le propriétaire est connu et qu'au moins un autre membre existe ; les publiques sans partage explicite
/// (aucune ligne) ne le sont jamais.
/// </summary>
public static class ShareClassifier
{
    public sealed record Result(string? OwnerId, IReadOnlyList<string> MemberIds)
    {
        public bool IsShared => OwnerId != null && MemberIds.Any(m => !string.Equals(m, OwnerId, StringComparison.Ordinal));
    }

    public static Result Classify(IEnumerable<(string UserId, UserItemShareLevel Level)> rows)
    {
        var members = new List<string>();
        string? owner = null;
        foreach (var (userId, level) in rows)
        {
            if (level < UserItemShareLevel.Read) continue;
            if (!members.Contains(userId, StringComparer.Ordinal)) members.Add(userId);
            if (level == UserItemShareLevel.ManageDelete && owner == null) owner = userId;
        }
        return new Result(owner, members);
    }
}
