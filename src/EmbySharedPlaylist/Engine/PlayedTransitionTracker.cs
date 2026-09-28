namespace EmbySharedPlaylist.Engine;

/// <summary>
/// Détecte la transition « non lu → lu » d'un couple (utilisateur, média) à partir des <c>UserDataSaved</c> (mémoire seulement,
/// LRU borné, thread-safe ; aucun accès base : chemin rapide du gestionnaire).
/// <list type="bullet">
/// <item><c>PlaybackStart</c> : mémorise la valeur <c>played</c> courante (jamais une transition).</item>
/// <item><c>TogglePlayed</c> avec <c>played=true</c> : transition certaine. Avec <c>played=false</c> : mémorise faux.</item>
/// <item>Motifs de LECTURE (<c>PlaybackProgress</c>, <c>PlaybackFinished</c>) avec <c>played=true</c> : transition si la dernière
/// valeur connue n'est pas <c>true</c> (inconnue = transition : le retrait est idempotent).</item>
/// <item>Tout autre motif (<c>Import</c>, <c>UpdateUserRating</c>, <c>UpdateHideFromResume</c>, motif inconnu) avec <c>played=true</c> :
/// mémoire inconnue = on MÉMORISE sans transition (une note ou un import ne doit pas retirer un média déjà lu) ; mémoire connue
/// <c>false</c> = vrai changement, donc transition ; mémoire <c>true</c> = rien.</item>
/// </list>
/// Un média déjà lu, relu jusqu'au bout, ne déclenche rien (Q1) : <c>PlaybackStart</c> mémorise <c>true</c>. Après une
/// transition la mémoire vaut <c>true</c> (un second <c>PlaybackFinished</c> ne redéclenche pas).
/// </summary>
public sealed class PlayedTransitionTracker
{
    public const int DefaultCapacity = 2000;

    private readonly object _lock = new();
    private readonly int _capacity;
    private readonly Dictionary<(string User, string Item), LinkedListNode<((string User, string Item) Key, bool Played)>> _index = new();
    private readonly LinkedList<((string User, string Item) Key, bool Played)> _order = new();

    public PlayedTransitionTracker(int capacity = DefaultCapacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Count
    {
        get { lock (_lock) return _index.Count; }
    }

    /// <summary>Vrai si l'événement est une transition non lu → lu à traiter.</summary>
    public bool OnUserData(string userId, string itemId, string? saveReason, bool played)
    {
        var key = (userId, itemId);
        lock (_lock)
        {
            var known = _index.TryGetValue(key, out var node) ? (bool?)node.Value.Played : null;

            if (string.Equals(saveReason, "PlaybackStart", StringComparison.OrdinalIgnoreCase))
            {
                Remember(key, played);
                return false;
            }

            bool transition;
            if (!played) transition = false;
            else if (string.Equals(saveReason, "TogglePlayed", StringComparison.OrdinalIgnoreCase)) transition = true; // geste volontaire, même si la mémoire dit « lu »
            else if (IsPlaybackReason(saveReason)) transition = known != true;                                            // inconnue = transition
            else transition = known == false;                                                                             // inconnue = mémoriser seulement
            Remember(key, played);
            return transition;
        }
    }

    private static bool IsPlaybackReason(string? reason) =>
        string.Equals(reason, "PlaybackProgress", StringComparison.OrdinalIgnoreCase)
        || string.Equals(reason, "PlaybackFinished", StringComparison.OrdinalIgnoreCase);

    private void Remember(( string User, string Item) key, bool played)
    {
        if (_index.TryGetValue(key, out var node))
        {
            _order.Remove(node);
            node.Value = (key, played);
            _order.AddFirst(node);
            return;
        }

        _index[key] = _order.AddFirst((key, played));
        while (_index.Count > _capacity)
        {
            var oldest = _order.Last!;
            _order.RemoveLast();
            _index.Remove(oldest.Value.Key);
        }
    }
}
