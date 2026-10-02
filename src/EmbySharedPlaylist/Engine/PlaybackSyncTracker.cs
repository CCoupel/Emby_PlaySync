using EmbySharedPlaylist.Core;

namespace EmbySharedPlaylist.Engine;

/// <summary>Décision du tracker pour un <c>PlaybackProgress</c> (v1.2.1, D23).</summary>
public enum PlaybackSyncDecision
{
    /// <summary>Transition lecture → pause : propager tout de suite (sans attendre l'intervalle).</summary>
    PauseTransition,
    /// <summary>Lecture en cours, intervalle minimal écoulé : propagation périodique.</summary>
    Periodic,
    /// <summary>Lecture en cours mais moins de <see cref="PlaybackSyncTracker.MinInterval"/> depuis la dernière propagation.</summary>
    Throttled,
    /// <summary>Heartbeat en pause, ou Progress tardif d'une session déjà fermée : aucune action.</summary>
    Ignored
}

/// <summary>
/// Suivi en mémoire de la session de lecture d'un couple (déclencheur, média) (v1.2.1, D23) ; remplace
/// <c>PauseTransitionTracker</c>. Logique pure : horloge injectée, LRU borné, thread-safe. Par couple : <c>PlaySessionId</c>,
/// dernier état de pause, date/position de la dernière propagation, session fermée, playlists cibles mémorisées (utiles
/// à la fin de lecture même si <c>remove-si-lu</c> a retiré le média entre-temps).
/// </summary>
public sealed class PlaybackSyncTracker
{
    public const int DefaultCapacity = 2000;

    /// <summary>Intervalle minimal entre deux propagations périodiques d'un même couple.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(10);

    /// <summary>Âge au-delà duquel les cibles mémorisées sont à re-résoudre.</summary>
    public static readonly TimeSpan TargetsMaxAge = TimeSpan.FromMinutes(5);

    private sealed class Session
    {
        public string PlaySessionId = string.Empty;
        public bool? LastPaused;
        public bool Closed;
        public DateTimeOffset? LastPropagatedAt;
        public long LastPropagatedTicks;
        public IReadOnlyList<string>? Targets;
        public DateTimeOffset TargetsAt;
    }

    private readonly object _lock = new();
    private readonly int _capacity;
    private readonly IClock _clock;
    private readonly Dictionary<(string User, string Item), LinkedListNode<((string User, string Item) Key, Session S)>> _index = new();
    private readonly LinkedList<((string User, string Item) Key, Session S)> _order = new();

    public PlaybackSyncTracker(IClock? clock = null, int capacity = DefaultCapacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _clock = clock ?? new SystemClock();
    }

    public int Count
    {
        get { lock (_lock) return _index.Count; }
    }

    /// <summary>
    /// <c>PlaybackStart</c> : ouvre (ou réinitialise) la session du couple avec ce <c>PlaySessionId</c>. Les cibles sont celles
    /// résolues par l'appelant (null = à résoudre plus tard).
    /// </summary>
    public void OnStart(string userId, string itemId, string? playSessionId, IReadOnlyList<string>? targets = null)
    {
        lock (_lock)
        {
            var s = new Session { PlaySessionId = playSessionId ?? string.Empty };
            if (targets != null) { s.Targets = targets; s.TargetsAt = _clock.UtcNow; }
            Put((userId, itemId), s);
        }
    }

    /// <summary>Classe un <c>PlaybackProgress</c>. La mémoire de pause est mise à jour dans tous les cas (sauf session fermée).</summary>
    public PlaybackSyncDecision OnProgress(string userId, string itemId, string? playSessionId, bool isPaused)
    {
        var key = (userId, itemId);
        var psid = playSessionId ?? string.Empty;
        lock (_lock)
        {
            var s = Get(key);
            if (s != null && s.Closed && psid.Length > 0 && string.Equals(s.PlaySessionId, psid, StringComparison.Ordinal))
                return PlaybackSyncDecision.Ignored; // Progress tardif d'une session déjà arrêtée (garde : jamais si psid vide, sinon le couple resterait muet)
            if (s == null || s.Closed || !string.Equals(s.PlaySessionId, psid, StringComparison.Ordinal))
            {
                s = new Session { PlaySessionId = psid }; // nouvelle session (Start manqué, redémarrage du plugin…)
                Put(key, s);
            }

            var wasPaused = s.LastPaused;
            s.LastPaused = isPaused;
            if (isPaused)
                return wasPaused == true ? PlaybackSyncDecision.Ignored : PlaybackSyncDecision.PauseTransition; // inconnu → transition
            if (s.LastPropagatedAt is DateTimeOffset last && _clock.UtcNow - last < MinInterval)
                return PlaybackSyncDecision.Throttled;
            return PlaybackSyncDecision.Periodic;
        }
    }

    /// <summary>
    /// Vrai si la session du couple est ouverte (non arrêtée) avec ce <c>PlaySessionId</c> (vide toléré). Re-testé par le moteur
    /// sous les verrous avant l'écriture d'un Progress périodique : un Stop/Completion concurrent ferme la session avant
    /// d'écrire, un Periodic tardif ne doit alors plus rien écrire.
    /// </summary>
    public bool IsOpen(string userId, string itemId, string? playSessionId)
    {
        var psid = playSessionId ?? string.Empty;
        lock (_lock)
        {
            var s = Get((userId, itemId));
            if (s == null || s.Closed) return false;
            return psid.Length == 0 || s.PlaySessionId.Length == 0 || string.Equals(s.PlaySessionId, psid, StringComparison.Ordinal);
        }
    }

    /// <summary>Note une propagation aboutie (démarre la minuterie de l'intervalle).</summary>
    public void MarkPropagated(string userId, string itemId, long ticks)
    {
        lock (_lock)
        {
            var s = Get((userId, itemId));
            if (s == null) return;
            s.LastPropagatedAt = _clock.UtcNow;
            s.LastPropagatedTicks = ticks;
        }
    }

    /// <summary>Mémorise les playlists cibles du couple (ids). À n'appeler que pour une VRAIE résolution (elle date les cibles).</summary>
    public void SetTargets(string userId, string itemId, IReadOnlyList<string> targets)
    {
        lock (_lock)
        {
            var s = Get((userId, itemId));
            if (s == null || s.Closed) return;
            s.Targets = targets;
            s.TargetsAt = _clock.UtcNow;
        }
    }

    /// <summary>Cibles mémorisées si elles existent, non vides et non périmées (<see cref="TargetsMaxAge"/>) ; sinon null (= à re-résoudre).</summary>
    public IReadOnlyList<string>? GetFreshTargets(string userId, string itemId)
    {
        lock (_lock)
        {
            var s = Get((userId, itemId));
            if (s == null || s.Closed || s.Targets == null || s.Targets.Count == 0) return null;
            return _clock.UtcNow - s.TargetsAt > TargetsMaxAge ? null : s.Targets;
        }
    }

    /// <summary>
    /// <c>PlaybackStopped</c> : ferme la session et renvoie les cibles mémorisées (même périmées : on préfère une cible de
    /// trop, relue fraîche, à une cible perdue). Si un autre <c>PlaySessionId</c> est déjà en cours (arrêt tardif d'une
    /// ancienne session) la session courante n'est PAS touchée et aucune cible n'est renvoyée.
    /// </summary>
    public IReadOnlyList<string> OnStop(string userId, string itemId, string? playSessionId)
    {
        var psid = playSessionId ?? string.Empty;
        lock (_lock)
        {
            var s = Get((userId, itemId));
            if (s == null)
            {
                Put((userId, itemId), new Session { PlaySessionId = psid, Closed = true });
                return Array.Empty<string>();
            }
            if (!s.Closed && !string.Equals(s.PlaySessionId, psid, StringComparison.Ordinal) && psid.Length > 0 && s.PlaySessionId.Length > 0)
                return Array.Empty<string>();
            var targets = s.Targets ?? Array.Empty<string>();
            s.Closed = true;
            s.PlaySessionId = psid.Length > 0 ? psid : s.PlaySessionId;
            return targets;
        }
    }

    /// <summary>Lecture d'un couple existant : rafraîchit sa position LRU (choix conscient, alignement sur l'ancien PauseTransitionTracker) (le plus récemment utilisé en tête).</summary>
    private Session? Get((string User, string Item) key)
    {
        if (!_index.TryGetValue(key, out var node)) return null;
        if (node != _order.First)
        {
            _order.Remove(node);
            _order.AddFirst(node);
        }
        return node.Value.S;
    }

    private void Put((string User, string Item) key, Session s)
    {
        if (_index.TryGetValue(key, out var node))
        {
            _order.Remove(node);
            node.Value = (key, s);
            _order.AddFirst(node);
            return;
        }
        _index[key] = _order.AddFirst((key, s));
        while (_index.Count > _capacity)
        {
            var oldest = _order.Last!;
            _order.RemoveLast();
            _index.Remove(oldest.Value.Key);
        }
    }
}
