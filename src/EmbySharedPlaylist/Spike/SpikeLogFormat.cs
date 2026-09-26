using EmbySharedPlaylist.Core;
using System.Globalization;

namespace EmbySharedPlaylist.Spike;

/// <summary>
/// Formatage pur des lignes de journal Emby du spike. Toutes les lignes commencent par « EmbySharedPlaylist »
/// (filtre : <c>grep EmbySharedPlaylist embyserver.txt</c>) et ne portent que des identifiants (jamais de nom d'utilisateur).
/// </summary>
public static class SpikeLogFormat
{
    public const string Prefix = "EmbySharedPlaylist spike : ";

    private static string B(bool? v) => v.HasValue ? (v.Value ? "true" : "false") : "null";
    private static string S(string? v) => string.IsNullOrEmpty(v) ? "-" : v;
    private static string L(long? v) => v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : "-";

    /// <summary>PlaybackProgress arrive toutes les quelques secondes par client : Debug, tout le reste en Info.</summary>
    public static bool IsDebugLevel(JournalEntry e) =>
        e.Kind == "UserDataSaved" && string.Equals(e.SaveReason, "PlaybackProgress", StringComparison.Ordinal);

    /// <summary>Ligne pour un événement observé (même contenu que l'entrée du journal mémoire).</summary>
    public static string Event(JournalEntry e) => e.Kind switch
    {
        "UserDataSaved" => $"{Prefix}UserDataSaved user={S(e.UserId)} item={S(e.ItemId)} reason={S(e.SaveReason)} " +
                           $"played={B(e.Played)} pos={L(e.PositionTicks)} pluginWrite={B(e.PluginWrite)}",
        "PlaylistItemsAdded" or "PlaylistItemsRemoved" or "PlaylistItemsMoved" =>
            $"{Prefix}{e.Kind} playlist={S(e.PlaylistId)} entry={S(e.EntryId)}" + (e.ItemId != null ? $" item={e.ItemId}" : string.Empty),
        "Probe" => $"{Prefix}Probe playlist={S(e.PlaylistId)} {S(e.Detail)}",
        "ItemUpdated" => $"{Prefix}ItemUpdated playlist={S(e.PlaylistId)} reason={S(e.SaveReason)}",
        _ => $"{Prefix}{S(e.Kind)}"
    };

    public static string Setup(string playlistId, string ownerUserId, int memberCount) =>
        $"{Prefix}Setup playlist={playlistId} owner={ownerUserId} members={memberCount.ToString(CultureInfo.InvariantCulture)}";

    public static string Policy(string userId, bool allowSharingPersonalItems) =>
        $"{Prefix}Policy user={userId} AllowSharingPersonalItems={B(allowSharingPersonalItems)}";

    public static string MarkPlayed(string userId, string itemId, bool played, bool asPlugin, bool playedAfter) =>
        $"{Prefix}MarkPlayed user={userId} item={itemId} played={B(played)} asPlugin={B(asPlugin)} playedAfter={B(playedAfter)}";

    public static string SetPosition(string userId, string itemId, long requestedTicks, long ticksAfter, bool playedAfter) =>
        $"{Prefix}SetPosition user={userId} item={itemId} requested={L(requestedTicks)} after={L(ticksAfter)} playedAfter={B(playedAfter)}";

    public static string RemoveItem(string playlistId, int removedCount, int entriesAfter) =>
        $"{Prefix}RemoveItem playlist={playlistId} removed={removedCount.ToString(CultureInfo.InvariantCulture)} entriesAfter={entriesAfter.ToString(CultureInfo.InvariantCulture)}";

    public static string TagsPost(string playlistId, int added, int removed, bool overviewChanged, int tagsAfter) =>
        $"{Prefix}Tags playlist={playlistId} add={added.ToString(CultureInfo.InvariantCulture)} remove={removed.ToString(CultureInfo.InvariantCulture)} " +
        $"overviewChanged={B(overviewChanged)} tagsAfter={tagsAfter.ToString(CultureInfo.InvariantCulture)}";
}
