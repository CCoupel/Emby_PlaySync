namespace EmbySharedPlaylist.Core;

/// <summary>Suivi (mémoire) de la durée des traitements de transition dans le gestionnaire : nombre, dernière et plus longue durée en ms.</summary>
public sealed class HandlerStats
{
    private readonly object _lock = new();
    private long _count;
    private long _last;
    private long _max;

    public void Record(long durationMs)
    {
        lock (_lock)
        {
            _count++;
            _last = durationMs;
            if (durationMs > _max) _max = durationMs;
        }
    }

    public (long Count, long LastMs, long MaxMs) Snapshot()
    {
        lock (_lock) return (_count, _last, _max);
    }
}
