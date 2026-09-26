namespace EmbySharedPlaylist.Core;

/// <summary>
/// Mémoire (aucune persistance) des playlists ayant subi la première détection depuis le démarrage, et compteurs de grâce
/// par playlist et par clé (<c>remove-si-lu</c>, <c>propager-lu</c>, <c>description</c>). Thread-safe, bornée :
/// au-delà de <c>capacity</c>, la playlist vue la plus anciennement ajoutée est oubliée (avec ses compteurs) —
/// elle subira alors une nouvelle première détection, sans conséquence (le plugin ne pose que ce qui manque).
/// </summary>
public sealed class SeenPlaylists
{
    public const int DefaultCapacity = 5000;

    private readonly object _lock = new();
    private readonly int _capacity;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly Dictionary<string, Dictionary<string, int>> _counters = new(StringComparer.Ordinal);

    public SeenPlaylists(int capacity = DefaultCapacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    /// <summary>Atomique : vrai UNE seule fois par id (le premier appelant gagne, avant toute écriture).</summary>
    public bool TryMarkSeen(string playlistId)
    {
        if (string.IsNullOrEmpty(playlistId)) throw new ArgumentException("playlistId requis", nameof(playlistId));
        lock (_lock)
        {
            if (!_seen.Add(playlistId)) return false;
            _order.Enqueue(playlistId);
            while (_seen.Count > _capacity)
            {
                var oldest = _order.Dequeue();
                _seen.Remove(oldest);
                _counters.Remove(oldest);
            }
            return true;
        }
    }

    public bool IsSeen(string playlistId)
    {
        lock (_lock) return _seen.Contains(playlistId);
    }

    /// <summary>Incrémente le compteur et renvoie sa nouvelle valeur (le compteur n'existe que pour une playlist vue).</summary>
    public int Bump(string playlistId, string key)
    {
        lock (_lock)
        {
            if (!_seen.Contains(playlistId)) return 0;
            if (!_counters.TryGetValue(playlistId, out var perKey)) _counters[playlistId] = perKey = new Dictionary<string, int>(StringComparer.Ordinal);
            perKey.TryGetValue(key, out var value);
            perKey[key] = ++value;
            return value;
        }
    }

    public void Reset(string playlistId, string key)
    {
        lock (_lock)
        {
            if (!_counters.TryGetValue(playlistId, out var perKey)) return;
            perKey.Remove(key);
            if (perKey.Count == 0) _counters.Remove(playlistId);
        }
    }

    public int Get(string playlistId, string key)
    {
        lock (_lock) return _counters.TryGetValue(playlistId, out var perKey) && perKey.TryGetValue(key, out var v) ? v : 0;
    }

    public int Count
    {
        get { lock (_lock) return _seen.Count; }
    }

    /// <summary>Copie pour Diagnostics/State : ids vus.</summary>
    public IReadOnlyList<string> SeenIds()
    {
        lock (_lock) return _order.Where(_seen.Contains).ToList();
    }

    /// <summary>Copie pour Diagnostics/State : compteurs non nuls par playlist.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Counters()
    {
        lock (_lock)
            return _counters.ToDictionary(p => p.Key, p => (IReadOnlyDictionary<string, int>)new Dictionary<string, int>(p.Value), StringComparer.Ordinal);
    }
}
