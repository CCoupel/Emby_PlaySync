namespace EmbySharedPlaylist.Engine;

/// <summary>
/// Détecte la transition « en lecture → en pause » (<c>IsPaused</c> false→true) d'un couple (utilisateur, média) à partir des
/// <c>ISessionManager.PlaybackProgress</c> (mémoire seulement, LRU borné, thread-safe). Distinct de
/// <see cref="PlayedTransitionTracker"/> : source d'événement différente (session, pas données utilisateur), sémantique
/// différente (booléen de pause, pas flag lu) — aucun couplage entre les deux concepts.
/// <c>OnProgress</c> renvoie vrai (transition à traiter) si l'état mémorisé n'était pas <c>true</c> et que <c>isPaused</c>
/// l'est (inconnu → transition, comme <see cref="PlayedTransitionTracker"/> : sur-déclencher plutôt que sous-déclencher,
/// sans risque grâce à l'idempotence de <c>SetPosition</c>). La mémoire est mise à jour dans tous les cas.
/// <c>PlaybackStopped</c> ne passe PAS par ce tracker (arrêt = événement discret, systématiquement tenté).
/// </summary>
public sealed class PauseTransitionTracker
{
    public const int DefaultCapacity = 2000;

    private readonly object _lock = new();
    private readonly int _capacity;
    private readonly Dictionary<(string User, string Item), LinkedListNode<((string User, string Item) Key, bool Paused)>> _index = new();
    private readonly LinkedList<((string User, string Item) Key, bool Paused)> _order = new();

    public PauseTransitionTracker(int capacity = DefaultCapacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Count
    {
        get { lock (_lock) return _index.Count; }
    }

    /// <summary>Vrai si l'événement est une transition lecture → pause à traiter.</summary>
    public bool OnProgress(string userId, string itemId, bool isPaused)
    {
        var key = (userId, itemId);
        lock (_lock)
        {
            var known = _index.TryGetValue(key, out var node) ? (bool?)node.Value.Paused : null;
            var transition = isPaused && known != true;
            Remember(key, isPaused);
            return transition;
        }
    }

    private void Remember((string User, string Item) key, bool paused)
    {
        if (_index.TryGetValue(key, out var node))
        {
            _order.Remove(node);
            node.Value = (key, paused);
            _order.AddFirst(node);
            return;
        }

        _index[key] = _order.AddFirst((key, paused));
        while (_index.Count > _capacity)
        {
            var oldest = _order.Last!;
            _order.RemoveLast();
            _index.Remove(oldest.Value.Key);
        }
    }
}
