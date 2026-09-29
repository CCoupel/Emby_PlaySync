namespace EmbySharedPlaylist.Core;

/// <summary>
/// Ensemble « écritures plugin » (anti-écho, R5) : le plugin enregistre (utilisateur, média) avant d'écrire ;
/// l'événement qui revient consomme l'entrée. Borné et thread-safe ; les entrées expirent après <c>ttl</c>.
/// <para>
/// v1.2.0 (B12, revue M2) : COMPTEUR par couple (et non plus ensemble). <see cref="Register"/> incrémente,
/// <see cref="TryConsume"/> décrémente (une consommation par écriture enregistrée), <see cref="Unregister"/> annule une
/// inscription dont l'écriture n'a finalement pas eu lieu. Deux écritures plugin successives chez le même membre (position
/// puis lu, propager-avancement + propager-lu) dont les échos arrivent après la libération du verrou
/// <see cref="UserItemLocks"/> restent ainsi chacune reconnues (R5). Le TTL court depuis la DERNIÈRE inscription du couple ;
/// à expiration, tout le compteur du couple est purgé.
/// </para>
/// </summary>
public sealed class PluginWriteTracker
{
    public const int DefaultCapacity = 1000;

    private readonly object _lock = new();
    private readonly Dictionary<(string UserId, string ItemId), (int Count, DateTime At)> _pending = new();
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
    // suivante sur le même couple. Comportement volontairement inchangé (impact limité, voir plan v0.3.0) ; à revoir si observé.
    public void Register(string userId, string itemId)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(itemId)) throw new ArgumentException("userId/itemId requis");
        lock (_lock)
        {
            var now = _now();
            Purge(now);
            // m4 : n'évincer que pour une clé NOUVELLE (incrémenter un couple existant n'augmente pas le nombre de clés).
            if (!_pending.ContainsKey((userId, itemId)) && _pending.Count >= _capacity)
            {
                var oldest = _pending.OrderBy(kv => kv.Value.At).First().Key;
                _pending.Remove(oldest);
            }
            _pending.TryGetValue((userId, itemId), out var current);
            _pending[(userId, itemId)] = (current.Count + 1, now);
        }
    }

    /// <summary>
    /// Annule UNE inscription (l'écriture enregistrée n'a pas eu lieu : accès perdu, position déjà identique, exception),
    /// pour ne pas laisser d'entrée « pending » jusqu'au TTL. Sans effet si le couple est absent ou expiré.
    /// </summary>
    /// <remarks>Sûr uniquement sous <see cref="UserItemLocks"/> du couple : sans ce verrou, il pourrait annuler l'inscription
    /// d'une AUTRE écriture concurrente du même couple.</remarks>
    public void Unregister(string userId, string itemId)
    {
        lock (_lock)
        {
            Purge(_now());
            Decrement((userId, itemId));
        }
    }

    /// <summary>Vrai (et consomme UNE inscription) si (utilisateur, média) avait été enregistré et n'a pas expiré.</summary>
    public bool TryConsume(string userId, string itemId)
    {
        lock (_lock)
        {
            Purge(_now());
            return Decrement((userId, itemId));
        }
    }

    private bool Decrement((string UserId, string ItemId) key)
    {
        if (!_pending.TryGetValue(key, out var entry)) return false;
        if (entry.Count <= 1) _pending.Remove(key);
        else _pending[key] = (entry.Count - 1, entry.At);
        return true;
    }

    public int Count
    {
        get { lock (_lock) { Purge(_now()); return _pending.Values.Sum(v => v.Count); } } // total des inscriptions en attente
    }

    private void Purge(DateTime now)
    {
        var expired = _pending.Where(kv => now - kv.Value.At > _ttl).Select(kv => kv.Key).ToList();
        foreach (var key in expired) _pending.Remove(key);
    }
}
