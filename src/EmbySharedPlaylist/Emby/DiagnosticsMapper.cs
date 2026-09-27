using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Reconciliation;

namespace EmbySharedPlaylist.Emby;

/// <summary>Transformations pures des données en mémoire vers les réponses de <c>Diagnostics/*</c> (testées sans SDK).</summary>
public static class DiagnosticsMapper
{
    /// <summary>Kinds des décisions du moteur, renvoyés par défaut par <see cref="Journal"/> (sans filtre <c>kind</c>).</summary>
    public static readonly string[] EngineKinds =
    {
        JournalEntries.ScanPass, JournalEntries.MarkerPosed, JournalEntries.DescriptionWritten, "MarkerSeen", "Removal",
        "Propagation", JournalEntries.Skipped, JournalEntries.Error
    };

    public static readonly string[] CounterKeys = { "remove-si-lu", "propager-lu", DefaultsService.DescriptionKey };

    /// <summary>
    /// Journal filtré. <paramref name="kinds"/> vide : uniquement les décisions du moteur ; sinon exactement les kinds demandés
    /// (liste séparée par des virgules, insensible à la casse).
    /// </summary>
    public static List<DiagnosticsJournalEntryDto> Journal(IEnumerable<JournalEntry> entries, string? kinds)
    {
        var wanted = (kinds ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) wanted = new HashSet<string>(EngineKinds, StringComparer.OrdinalIgnoreCase);

        return entries.Where(e => wanted.Contains(e.Kind))
            .Select(e => new DiagnosticsJournalEntryDto
            {
                Ts = e.Ts, Kind = e.Kind, UserId = e.UserId, ItemId = e.ItemId, PlaylistId = e.PlaylistId, Detail = e.Detail
            }).ToList();
    }

    public static DiagnosticsStateDto State(SeenPlaylists seen, LastPassInfo? lastPass, (long Count, long LastMs, long MaxMs) handler, int gracePasses,
        IReadOnlyDictionary<string, long>? skipped = null)
    {
        var counters = seen.Counters();
        return new DiagnosticsStateDto
        {
            SeenPlaylistIds = seen.SeenIds().ToList(),
            // Toutes les clés sont présentes (0 si absente) : les tests d'intégration lisent un compteur précis.
            GraceCounters = counters.ToDictionary(
                p => p.Key,
                p => CounterKeys.ToDictionary(k => k, k => p.Value.TryGetValue(k, out var v) ? v : 0),
                StringComparer.Ordinal),
            LastPass = lastPass == null
                ? new DiagnosticsLastPassDto()
                : new DiagnosticsLastPassDto
                {
                    Ts = lastPass.Ts.UtcDateTime.ToString("o"),
                    DurationMs = lastPass.DurationMs,
                    PlaylistsSeen = lastPass.PlaylistsSeen,
                    SharedManaged = lastPass.SharedManaged
                },
            Handler = new DiagnosticsHandlerDto { Count = handler.Count, LastMs = handler.LastMs, MaxMs = handler.MaxMs },
            GracePasses = gracePasses,
            SkippedCounts = skipped == null ? new Dictionary<string, long>() : new Dictionary<string, long>(skipped)
        };
    }
}
