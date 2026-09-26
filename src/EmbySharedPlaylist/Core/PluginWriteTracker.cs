namespace EmbySharedPlaylist.Core;

/// <summary>
/// Ensemble « écritures plugin » (anti-écho, R5) : le plugin enregistre (utilisateur, média) avant d'écrire ;
/// l'événement qui revient consomme l'entrée. Borné et thread-safe ; les entrées expirent après <c>ttl</c>.
/// </summary>
public sealed class PluginWriteTracker
{
    public const int DefaultCapacity = 1000;

    private readonly object _lock = new();
    private readonly Dictionary<(long UserId, long ItemId), DateTime> _pending = new();
    private readonly TimeSpan _ttl;
    private readonly int _capacity;
    private readonly Func<DateTime> _now;

    public PluginWriteTracker(TimeSpan? ttl = null, int capacity = DefaultCapacity, Func<DateTime>? now = null)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _ttl = ttl ?? TimeSpan.FromMinutes(5);
        _capacity = capacity;
        _now = now ?? (() => DateTime.UtcNow);
    }

    // TODO (#21, anti-écho) : une écriture enregistrée dont l'événement n'est jamais émis (donnée inchangée) reste
    // en attente jusqu'à expiration (TTL) et peut marquer à tort pluginWrite=true une écriture utilisateur
    // suivante sur le même couple. Comportement volontairement inchangé pour le spike ; à traiter avec #21.
    public void Register(long userId, long itemId)
    {
        lock (_lock)
        {
            var now = _now();
            Purge(now);
            if (_pending.Count >= _capacity)
            {
                var oldest = _pending.OrderBy(kv => kv.Value).First().Key;
                _pending.Remove(oldest);
            }
            _pending[(userId, itemId)] = now;
        }
    }

    /// <summary>Vrai (et consomme l'entrée) si (utilisateur, média) avait été enregistré et n'a pas expiré.</summary>
    public bool TryConsume(long userId, long itemId)
    {
        lock (_lock)
        {
            Purge(_now());
            return _pending.Remove((userId, itemId));
        }
    }

    public int Count
    {
        get { lock (_lock) { Purge(_now()); return _pending.Count; } }
    }

    private void Purge(DateTime now)
    {
        var expired = _pending.Where(kv => now - kv.Value > _ttl).Select(kv => kv.Key).ToList();
        foreach (var key in expired) _pending.Remove(key);
    }
}
