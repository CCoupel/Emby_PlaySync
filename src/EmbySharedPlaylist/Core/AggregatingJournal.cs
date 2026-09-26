namespace EmbySharedPlaylist.Core;

/// <summary>
/// Journal décorateur : compte TOUS les <c>Skipped</c> par raison (<see cref="SkippedCounters"/>, exposé par Diagnostics/State) et
/// n'inscrit dans le journal borné (500) que les cas informatifs. Les raisons bruyantes — une par événement de playlist ou par
/// lecture : <c>already-seen</c>, <c>reentrant</c>, <c>not-shared</c>, <c>unknown-owner</c> — ne vont qu'aux compteurs et, en Debug,
/// au fichier (jamais en Info ni sur la console). Les autres entrées passent telles quelles.
/// </summary>
public sealed class AggregatingJournal : IJournal
{
    /// <summary>Raisons de <c>Skipped</c> agrégées seulement (pas d'entrée de journal).</summary>
    public static readonly IReadOnlySet<string> NoisyReasons = new HashSet<string>(StringComparer.Ordinal)
    {
        "already-seen", "reentrant", "not-shared", "unknown-owner"
    };

    private readonly IJournal _inner;
    private readonly SkippedCounters _counters;
    private readonly Log? _log;

    public AggregatingJournal(IJournal inner, SkippedCounters counters, Log? log = null)
    {
        _inner = inner;
        _counters = counters;
        _log = log;
    }

    public void Add(JournalEntry entry)
    {
        if (entry.Kind == JournalEntries.Skipped)
        {
            var reason = entry.Detail ?? "unknown";
            _counters.Increment(reason);
            if (NoisyReasons.Contains(reason))
            {
                _log?.Debug(LogFormat.Entry(entry));
                return;
            }
        }
        _inner.Add(entry);
    }
}
