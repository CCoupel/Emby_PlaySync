namespace EmbySharedPlaylist.Core;

/// <summary>Fabrique des entrées de journal du moteur (ids et compteurs seulement, jamais de message brut d'exception).</summary>
public static class JournalEntries
{
    public const string ScanPass = "ScanPass";
    public const string MarkerPosed = "MarkerPosed";
    public const string DescriptionWritten = "DescriptionWritten";
    public const string Skipped = "Skipped";
    public const string Error = "Error";

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

    private static JournalEntry Fill(JournalEntry e, string? playlistId, string? detail)
    {
        e.PlaylistId = playlistId;
        e.Detail = detail;
        return e;
    }
}
