using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Marker;

namespace EmbySharedPlaylist.Reconciliation;

/// <summary>Ce qu'a fait un traitement de <see cref="DefaultsService"/> (pour la passe et les tests).</summary>
public sealed record DefaultsOutcome(int MarkersPosed, bool DescriptionWritten, bool Skipped, int PendingGrace)
{
    public static readonly DefaultsOutcome SkippedOutcome = new(0, false, true, 0);
}

/// <summary>
/// Pose des étiquettes par défaut (<c>remove-si-lu=NON</c>, <c>propager-lu=NON</c>) et du message d'aide pour une playlist gérée.
/// <list type="bullet">
/// <item><b>Première détection</b> (id absent de la mémoire) : la playlist est marquée « vue » <b>avant</b> toute écriture (atomique),
/// puis pose immédiate de chaque famille absente et du message si la description est vide.</item>
/// <item><b>Playlist déjà vue</b> (<see cref="OnPass"/>) : pour chaque famille absente, compteur++ ; à <c>gracePasses</c> passes
/// consécutives, pose et remise à zéro (idem pour la description vide) ; famille présente : compteur remis à zéro.</item>
/// </list>
/// Tout se fait sous le verrou de la playlist, dans un <see cref="WriteScope"/>, en UNE lecture-écriture
/// (<see cref="IPlaylistGateway.ApplyDefaults"/> re-vérifie à l'écriture). Jamais de suppression d'étiquette. Une écriture
/// en échec est journalisée (<c>Error</c>) et n'est jamais propagée.
/// </summary>
public sealed class DefaultsService
{
    private static readonly MarkerFamily[] Families = { MarkerFamily.RemoveSiLu, MarkerFamily.PropagerLu };
    public const string DescriptionKey = "description";

    private readonly IPlaylistGateway _gateway;
    private readonly SeenPlaylists _seen;
    private readonly PlaylistLocks _locks;
    private readonly IJournal _journal;
    private readonly string _helpText;
    private readonly Func<int> _gracePasses;
    private readonly IClock? _clock;
    private readonly TimeSpan _lockTimeout;
    private readonly Func<bool> _isSuspended;

    public DefaultsService(IPlaylistGateway gateway, SeenPlaylists seen, PlaylistLocks locks, IJournal journal, string helpText, int gracePasses)
        : this(gateway, seen, locks, journal, helpText, () => gracePasses)
    {
    }

    /// <param name="gracePasses">Lu à chaque passe (la configuration peut changer ; borné à 1 minimum).</param>
    public DefaultsService(IPlaylistGateway gateway, SeenPlaylists seen, PlaylistLocks locks, IJournal journal, string helpText,
        Func<int> gracePasses, IClock? clock = null, TimeSpan? lockTimeout = null, Func<bool>? isSuspended = null)
    {
        _gateway = gateway;
        _seen = seen;
        _locks = locks;
        _journal = journal;
        _helpText = helpText;
        _gracePasses = gracePasses;
        _clock = clock;
        _lockTimeout = lockTimeout ?? PlaylistLocks.DefaultTimeout;
        _isSuspended = isSuspended ?? (() => false);
    }

    public DefaultsOutcome OnFirstDetection(PlaylistSnapshot p)
    {
        // Suspendu (sonde U11 active) : aucune lecture ni écriture, la playlist n'est PAS marquée vue.
        if (_isSuspended()) return DefaultsOutcome.SkippedOutcome;
        using var gate = _locks.TryAcquire(p.Id, _lockTimeout);
        if (gate == null) return Skip(p.Id, "lock-busy");

        // Marquée « vue » AVANT l'écriture : l'écho de notre propre écriture ne peut pas déclencher une seconde première détection.
        if (!_seen.TryMarkSeen(p.Id)) return Skip(p.Id, "already-seen");

        var families = Families.Where(f => MarkerEvaluator.Evaluate(p.Tags, f) == MarkerState.None).ToList();
        var overview = string.IsNullOrWhiteSpace(p.Overview) ? _helpText : null;
        if (families.Count == 0 && overview == null) return Skip(p.Id, "marker-present");

        return Write(p, families, overview, "first-detection", pending: 0);
    }

    public DefaultsOutcome OnPass(PlaylistSnapshot p)
    {
        if (_isSuspended()) return DefaultsOutcome.SkippedOutcome;
        using var gate = _locks.TryAcquire(p.Id, _lockTimeout);
        if (gate == null) return Skip(p.Id, "lock-busy");

        // Une playlist jamais vue subit d'abord la première détection (verrou réentrant : même fil).
        if (!_seen.IsSeen(p.Id)) return OnFirstDetection(p);

        // Relecture fraîche SOUS le verrou : l'instantané reçu a pu être pris avant l'écriture d'un autre fil (un instantané
        // périmé ferait avancer les compteurs de grâce à tort). Repli sur l'instantané reçu si la relecture ne donne rien.
        p = _gateway.Get(p.Id) ?? p;

        var grace = Math.Max(1, _gracePasses());
        var toPose = new List<MarkerFamily>();
        var pending = 0;
        foreach (var f in Families)
        {
            var key = MarkerEvaluator.FamilyName(f);
            if (MarkerEvaluator.Evaluate(p.Tags, f) != MarkerState.None) { _seen.Reset(p.Id, key); continue; }
            if (_seen.Bump(p.Id, key) >= grace) toPose.Add(f); else pending++;
        }

        string? overview = null;
        if (string.IsNullOrWhiteSpace(p.Overview))
        {
            if (_seen.Bump(p.Id, DescriptionKey) >= grace) overview = _helpText; else pending++;
        }
        else _seen.Reset(p.Id, DescriptionKey);

        if (toPose.Count == 0 && overview == null) return new DefaultsOutcome(0, false, false, pending);
        return Write(p, toPose, overview, "grace-elapsed", pending);
    }

    private DefaultsOutcome Write(PlaylistSnapshot p, List<MarkerFamily> families, string? overview, string cause, int pending)
    {
        ApplyResult result;
        try
        {
            using (WriteScope.Enter())
            {
                result = _gateway.ApplyDefaults(p.Id, families, overview) ?? new ApplyResult();
            }
        }
        catch (Exception ex)
        {
            _journal.Add(JournalEntries.ErrorEntry(_clock, p.Id, ex));
            return new DefaultsOutcome(0, false, false, pending);
        }

        foreach (var f in result.Posed)
        {
            _journal.Add(JournalEntries.Of(_clock, JournalEntries.MarkerPosed, p.Id, $"family={MarkerEvaluator.FamilyName(f)} cause={cause}"));
            _seen.Reset(p.Id, MarkerEvaluator.FamilyName(f));
        }
        if (result.OverviewWritten)
        {
            _journal.Add(JournalEntries.Of(_clock, JournalEntries.DescriptionWritten, p.Id, $"cause={cause}"));
            _seen.Reset(p.Id, DescriptionKey);
        }
        return new DefaultsOutcome(result.Posed.Count, result.OverviewWritten, false, pending);
    }

    private DefaultsOutcome Skip(string playlistId, string reason)
    {
        _journal.Add(JournalEntries.SkippedEntry(_clock, playlistId, reason));
        return DefaultsOutcome.SkippedOutcome;
    }
}
