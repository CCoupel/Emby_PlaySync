namespace EmbySharedPlaylist.Core;

/// <summary>Compteurs agrégés (mémoire) des <c>Skipped</c> par raison : ils remplacent une entrée de journal par événement pour les raisons bruyantes.</summary>
public sealed class SkippedCounters
{
    private readonly object _lock = new();
    private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);

    public void Increment(string reason)
    {
        if (string.IsNullOrEmpty(reason)) reason = "unknown";
        lock (_lock)
        {
            _counts.TryGetValue(reason, out var n);
            _counts[reason] = n + 1;
        }
    }

    public IReadOnlyDictionary<string, long> Snapshot()
    {
        lock (_lock) return new Dictionary<string, long>(_counts, StringComparer.Ordinal);
    }
}
