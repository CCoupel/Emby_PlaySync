namespace EmbySharedPlaylist.Core;

/// <summary>Fabrique des entrées de journal du moteur (ids et compteurs seulement, jamais de message brut d'exception).</summary>
public static class JournalEntries
{
    public const string ScanPass = "ScanPass";
    public const string MarkerPosed = "MarkerPosed";
    public const string DescriptionWritten = "DescriptionWritten";
    public const string Skipped = "Skipped";
    public const string Error = "Error";

    /// <summary>v1.1.0 (#39, D20) : membre ajouté/modifié/retiré par le propriétaire depuis la page utilisateur.
    /// <c>UserId</c> = membre concerné (jamais le propriétaire, qui est le demandeur implicite).</summary>
    public const string ShareChanged = "ShareChanged";

    /// <summary>v1.1.0 (#39, D19) : bascule explicite d'une option par le propriétaire. <c>UserId</c> = propriétaire.</summary>
    public const string MarkerSet = "MarkerSet";

    private static JournalEntry New(IClock? clock, string kind) => new()
    {
        Ts = (clock?.UtcNow ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("o"),
        Kind = kind
    };

    public static JournalEntry SkippedEntry(IClock? clock, string? playlistId, string reason) =>
        Fill(New(clock, Skipped), playlistId, reason);

    /// <summary>Le détail est le seul type d'exception : le message peut contenir des noms ou des chemins.</summary>
    public static JournalEntry ErrorEntry(IClock? clock, string? playlistId, Exception ex) =>
        Fill(New(clock, Error), playlistId, ex.GetType().Name);

    public static JournalEntry Of(IClock? clock, string kind, string? playlistId, string? detail) =>
        Fill(New(clock, kind), playlistId, detail);

    /// <summary><c>ShareChanged</c> (v1.1.0) : <paramref name="memberUserId"/> = membre concerné, ids seulement
    /// (jamais de nom) — <paramref name="detail"/> attendu : <c>action=add|update|remove level=Read|Write|-</c>.</summary>
    public static JournalEntry ShareChangedEntry(IClock? clock, string? playlistId, string? memberUserId, string? detail) =>
        FillWithUser(New(clock, ShareChanged), playlistId, memberUserId, detail);

    /// <summary><c>MarkerSet</c> (v1.1.0) : <paramref name="ownerUserId"/> = propriétaire (action explicite, D19) —
    /// <paramref name="detail"/> attendu : <c>family=&lt;f&gt; value=OUI|NON removed=&lt;n&gt;</c>.</summary>
    public static JournalEntry MarkerSetEntry(IClock? clock, string? playlistId, string? ownerUserId, string? detail) =>
        FillWithUser(New(clock, MarkerSet), playlistId, ownerUserId, detail);

    private static JournalEntry Fill(JournalEntry e, string? playlistId, string? detail)
    {
        e.PlaylistId = playlistId;
        e.Detail = detail;
        return e;
    }

    private static JournalEntry FillWithUser(JournalEntry e, string? playlistId, string? userId, string? detail)
    {
        e.PlaylistId = playlistId;
        e.UserId = userId;
        e.Detail = detail;
        return e;
    }
}
