using System.Diagnostics;
using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Marker;
using EmbySharedPlaylist.Reconciliation;

namespace EmbySharedPlaylist.Engine;

/// <summary>Bilan d'une propagation de position. <c>CandidatePlaylistIds</c> : playlists gérées contenant le média (à mémoriser) ; <c>LockBusy</c> : verrous non obtenus (playlist ou membre).</summary>
public sealed record PositionPropagationResult(int Candidates, int PlaylistsChanged, int Propagated, int Skipped, long DurationMs,
    IReadOnlyList<string>? CandidatePlaylistIds = null, int LockBusy = 0);

/// <summary>Origine d'une propagation de position (v1.2.1, D23) ; le libellé (<c>trigger=</c>) termine le <c>Detail</c> journalisé.</summary>
public enum PositionTrigger
{
    /// <summary><c>PlaybackProgress</c> périodique : verrous 250 ms, aucun journal (compteurs).</summary>
    Periodic,
    Pause,
    Stop,
    /// <summary>Fin de lecture (<c>PlayedToCompletion</c>) : 0 si <c>propager-lu=OUI</c>, sinon position brute.</summary>
    Completion
}

/// <summary>
/// Propagation de la position de lecture (#45), cousin de <see cref="ReadRemovalEngine"/> mais issu d'un flux d'événements
/// SÉPARÉ (<c>ISessionManager.PlaybackProgress</c>/<c>PlaybackStopped</c>, pas <c>UserDataSaved</c>) : classe distincte, pas
/// une fusion. Déclenché (v1.2.1, D23) pendant la lecture (Progress périodique, ≤ 1/10 s par couple, <see cref="PlaybackSyncTracker"/>), à la
/// pause (transition), à l'arrêt et en fin de lecture (<see cref="PositionTrigger"/>). Pour chaque playlist gérée dont il est membre et qui contient le média : première
/// détection si non vue, relecture fraîche des étiquettes, et si <c>propager-avancement=OUI</c> SEULE (v1.2.0, D21 : famille
/// dédiée, indépendante de <c>propager-lu</c> et de <c>remove-si-lu</c>) : pour chaque AUTRE membre avec accès, pose la
/// position BRUTE (anti-écho <see cref="PluginWriteTracker"/>, dernier écrit gagne, aucune écriture si déjà cette
/// position), quel que soit l'état lu du déclencheur ou du membre (#57). L'écriture chez un membre est sérialisée par
/// <see cref="UserItemLocks"/>. Le flag lu et la position du déclencheur ne sont jamais modifiés. Même verrou par playlist (un seul à la fois), même ordre de grandeur
/// de budget que le retrait/la propagation du lu (constante partagée, instance de minuteur propre à cet appel).
/// <see cref="Handle"/> ne lève jamais.
/// </summary>
public sealed class PlaybackPositionEngine
{
    /// <summary>Positions absolues en deçà de ce seuil sont ignorées (bruit, ~30 s) ; non appliqué à la remise à 0 de fin de lecture.</summary>
    public static readonly long MinPositionTicks = TimeSpan.FromSeconds(30).Ticks;

    /// <summary>Délai d'attente des verrous sur un Progress périodique : ne jamais bloquer le pipeline de progression d'Emby.</summary>
    public static readonly TimeSpan PeriodicLockTimeout = TimeSpan.FromMilliseconds(250);

    private readonly IPlaylistGateway _gateway;
    private readonly IUserDataGateway _userData;
    private readonly PluginWriteTracker _writeTracker;
    private readonly DefaultsService _defaults;
    private readonly SeenPlaylists _seen;
    private readonly PlaylistLocks _locks;
    private readonly IJournal _journal;
    private readonly IClock _clock;
    private readonly TimeSpan _lockTimeout;
    private readonly TimeSpan _budget;
    private readonly HandlerStats? _handler;
    private readonly UserItemLocks _userItemLocks;
    private readonly PositionProgressCounters? _progressCounters;

    public PlaybackPositionEngine(IPlaylistGateway gateway, IUserDataGateway userData, PluginWriteTracker writeTracker,
        DefaultsService defaults, SeenPlaylists seen, PlaylistLocks locks, IJournal journal, IClock clock,
        TimeSpan? lockTimeout = null, TimeSpan? budget = null, HandlerStats? handler = null, UserItemLocks? userItemLocks = null,
        PositionProgressCounters? progressCounters = null)
    {
        _gateway = gateway;
        _userData = userData;
        _writeTracker = writeTracker;
        _defaults = defaults;
        _seen = seen;
        _locks = locks;
        _journal = journal;
        _clock = clock;
        _lockTimeout = lockTimeout ?? PlaylistLocks.DefaultTimeout;
        _budget = budget ?? ReadRemovalEngine.DefaultBudget;
        _handler = handler;
        _userItemLocks = userItemLocks ?? new UserItemLocks();
        _progressCounters = progressCounters;
    }

    /// <summary>
    /// Revue C1 : la durée est enregistrée dans <see cref="HandlerStats"/> (même instance partagée que
    /// <c>PlaybackEventProcessor</c> côté flux du lu) pour CHAQUE appel, quel que soit le chemin de sortie — le contrat
    /// (<c>Diagnostics/State.Handler</c>) annonce les deux flux d'événements confondus.
    /// </summary>
    public PositionPropagationResult Handle(string userId, string itemId, long ticks,
        PositionTrigger trigger = PositionTrigger.Stop, IReadOnlyCollection<string>? targets = null,
        Func<bool>? isSessionOpen = null)
    {
        var total = Stopwatch.StartNew();
        var candidates = 0;
        var changed = 0;
        var propagated = 0;
        var skipped = 0;
        var lockBusy = 0;
        IReadOnlyList<string> candidateIds = Array.Empty<string>();

        try
        {
            try
            {
                var playlists = ResolvePlaylists(userId, itemId, trigger, targets);
                candidates = playlists.Count;
                candidateIds = playlists.Select(p => p.Id).ToList();
                for (var index = 0; index < playlists.Count; index++)
                {
                    var snapshot = playlists[index];
                    if (total.Elapsed >= _budget)
                    {
                        skipped += playlists.Count - index;
                        JournalFor(trigger, JournalEntries.SkippedEntry(_clock, snapshot.Id, "budget-exceeded"));
                        break;
                    }
                    try
                    {
                        var outcome = HandlePlaylist(snapshot, userId, itemId, ticks, trigger, total, ref lockBusy, isSessionOpen);
                        if (outcome < 0) skipped++;
                        else if (outcome > 0) { changed++; propagated += outcome; }
                        else skipped++;
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        Journal(JournalEntries.ErrorEntry(_clock, snapshot.Id, ex)); // une playlist en erreur n'arrête pas les suivantes
                    }
                }
            }
            catch (Exception ex)
            {
                Journal(JournalEntries.ErrorEntry(_clock, null, ex));
            }

            if (trigger == PositionTrigger.Periodic)
            {
                if (lockBusy > 0) _progressCounters?.IncrementLockBusy();
                else if (propagated > 0) _progressCounters?.IncrementPropagated();
            }
            return new PositionPropagationResult(candidates, changed, propagated, skipped, total.ElapsedMilliseconds, candidateIds, lockBusy);
        }
        finally
        {
            // v1.2.1 (I1) : les Progress périodiques (~1 / 10 s / couple) ne sont pas mesurés, ils biaiseraient State.Handler.
            if (trigger != PositionTrigger.Periodic) _handler?.Record(total.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Ids des playlists gérées où <paramref name="userId"/> est membre et qui contiennent le média (ouverture de session,
    /// mémorisation des cibles). Ne lève jamais : liste vide en cas d'erreur (journalisée).
    /// </summary>
    public IReadOnlyList<string> ResolveTargets(string userId, string itemId)
    {
        try { return _gateway.ListSharedPlaylistsOfUserContaining(userId, itemId).Select(p => p.Id).ToList(); }
        catch (Exception ex)
        {
            Journal(JournalEntries.ErrorEntry(_clock, null, ex));
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Périodique avec cibles mémorisées : on relit ces playlists (pas de résolution coûteuse à chaque Progress). Fin de
    /// lecture : cibles mémorisées ∪ playlists contenant encore le média (le média a pu être retiré par remove-si-lu
    /// avant l'arrêt). Autres cas : playlists contenant le média.
    /// </summary>
    private IReadOnlyList<PlaylistSnapshot> ResolvePlaylists(string userId, string itemId, PositionTrigger trigger, IReadOnlyCollection<string>? targets)
    {
        if (trigger == PositionTrigger.Periodic && targets != null)
            return FromIds(targets, new List<PlaylistSnapshot>());

        var current = _gateway.ListSharedPlaylistsOfUserContaining(userId, itemId);
        if (trigger != PositionTrigger.Completion || targets == null || targets.Count == 0) return current;
        var merged = new List<PlaylistSnapshot>(current);
        return FromIds(targets.Where(id => merged.All(p => !string.Equals(p.Id, id, StringComparison.Ordinal))).ToList(), merged);
    }

    private IReadOnlyList<PlaylistSnapshot> FromIds(IReadOnlyCollection<string> ids, List<PlaylistSnapshot> into)
    {
        foreach (var id in ids)
        {
            var snap = _gateway.Get(id);
            if (snap != null) into.Add(snap);
        }
        return into;
    }

    /// <returns>Nombre de membres propagés (&gt; 0), 0 si rien propagé (inactif/déjà à cette position/personne), -1 si passée (verrou occupé).</returns>
    private int HandlePlaylist(PlaylistSnapshot snapshot, string userId, string itemId, long ticks, PositionTrigger trigger,
        Stopwatch total, ref int lockBusyTotal, Func<bool>? isSessionOpen)
    {
        var sw = Stopwatch.StartNew();
        var timeout = trigger == PositionTrigger.Periodic && PeriodicLockTimeout < _lockTimeout ? PeriodicLockTimeout : _lockTimeout;
        using var gate = _locks.TryAcquire(snapshot.Id, timeout);
        if (gate == null)
        {
            lockBusyTotal++;
            JournalFor(trigger, JournalEntries.SkippedEntry(_clock, snapshot.Id, "lock-busy"));
            return -1;
        }

        // Progress périodique tardif (course avec Stop/Completion) : la session a pu être fermée entre la décision et le
        // verrou ; abandon silencieux pour ne pas écraser la position de fin (0 ou position d'arrêt).
        if (trigger == PositionTrigger.Periodic && isSessionOpen != null && !isSessionOpen()) return 0;

        // Première détection AVANT l'évaluation (pose des NON, message d'aide) ; verrou réentrant : même fil.
        if (!_seen.IsSeen(snapshot.Id)) _defaults.OnFirstDetection(snapshot);

        // Relecture fraîche : les étiquettes sont lues à l'événement, jamais mises en cache.
        var fresh = _gateway.Get(snapshot.Id) ?? snapshot;
        if (MarkerEvaluator.Evaluate(fresh.Tags, MarkerFamily.PropagerAvancement) != MarkerState.Oui)
        {
            JournalFor(trigger, JournalEntries.SkippedEntry(_clock, snapshot.Id, "inactive"));
            return 0;
        }

        // Fin de lecture (D23) : propager-lu=OUI => 0 (miroir du déclencheur : lu, plus de point de reprise), sinon position
        // d'arrêt brute (S9f) soumise au seuil de 30 s. Couplage sur la CONFIGURATION de la playlist, jamais sur l'état lu.
        var writeTicks = ticks;
        if (trigger == PositionTrigger.Completion)
        {
            if (MarkerEvaluator.Evaluate(fresh.Tags, MarkerFamily.PropagerLu) == MarkerState.Oui) writeTicks = 0;
            else if (ticks < MinPositionTicks)
            {
                JournalFor(trigger, JournalEntries.SkippedEntry(_clock, snapshot.Id, "too-short"));
                return 0;
            }
        }

        var propagated = 0;
        var samePosition = 0;
        var noAccess = 0;
        var lockBusy = 0;

        foreach (var memberId in fresh.MemberIds)
        {
            if (string.Equals(memberId, userId, StringComparison.Ordinal)) continue;
            if (total.Elapsed >= _budget) break; // budget global partagé : reprise au prochain événement

            // Verrou (utilisateur, média) le plus interne (B7) : lecture de la position, anti-écho et écriture dans la même
            // section, pour qu'un lu propagé en parallèle chez ce membre ne soit ni écrasé ni perdu.
            using var userGate = _userItemLocks.TryAcquire(memberId, itemId, timeout);
            if (userGate == null)
            {
                lockBusy++;
                JournalFor(trigger, SkippedForMember(memberId, snapshot.Id, itemId, "lock-busy"));
                continue;
            }

            if (trigger == PositionTrigger.Periodic && isSessionOpen != null && !isSessionOpen()) break; // idem, sous le verrou du membre

            if (!_userData.HasAccess(memberId, itemId))
            {
                noAccess++;
                JournalFor(trigger, SkippedForMember(memberId, snapshot.Id, itemId, "no-access")); // R8
                continue;
            }

            // Vérifié AVANT d'enregistrer l'anti-écho : n'enregistrer que si une écriture réelle va suivre (sinon
            // l'entrée du tracker resterait "pending" sans jamais être consommée — même piège que la revue A1 sur #20).
            if (_userData.GetPosition(memberId, itemId) == writeTicks)
            {
                samePosition++;
                JournalFor(trigger, SkippedForMember(memberId, snapshot.Id, itemId, "same-position"));
                continue;
            }

            _writeTracker.Register(memberId, itemId);
            var written = false;
            try { written = _userData.SetPosition(memberId, itemId, writeTicks); }
            finally { if (!written) _writeTracker.Unregister(memberId, itemId); } // pas d'écriture => pas d'écho attendu (m2)
            if (written) propagated++;
            else samePosition++; // résiduel : position redevenue identique entre notre lecture et l'écriture (rare)
        }

        lockBusyTotal += lockBusy;
        // Progress périodique : aucune entrée (compteurs Diagnostics/State.PositionProgress) — le journal est borné à 500.
        if (trigger != PositionTrigger.Periodic)
        {
            var detail = $"members={fresh.MemberIds.Count(m => !string.Equals(m, userId, StringComparison.Ordinal))} " +
                         $"propagated={propagated} samePosition={samePosition} noAccess={noAccess} lockBusy={lockBusy} durationMs={sw.ElapsedMilliseconds} " +
                         $"trigger={TriggerLabel(trigger)}";
            var entry = JournalEntries.Of(_clock, "PositionPropagation", snapshot.Id, detail);
            entry.UserId = userId;
            entry.ItemId = itemId;
            Journal(entry);
        }
        return propagated;
    }

    public static string TriggerLabel(PositionTrigger trigger) => trigger switch
    {
        PositionTrigger.Pause => "pause",
        PositionTrigger.Completion => "completion",
        PositionTrigger.Periodic => "periodic",
        _ => "stop"
    };

    /// <summary>Les Skipped d'un Progress périodique ne sont pas journalisés (bruit) ; ceux des événements discrets le sont.</summary>
    private void JournalFor(PositionTrigger trigger, JournalEntry entry)
    {
        if (trigger != PositionTrigger.Periodic) Journal(entry);
    }

    private JournalEntry SkippedForMember(string memberId, string playlistId, string itemId, string reason)
    {
        var entry = JournalEntries.SkippedEntry(_clock, playlistId, reason);
        entry.UserId = memberId;
        entry.ItemId = itemId;
        return entry;
    }

    private void Journal(JournalEntry entry)
    {
        try { _journal.Add(entry); } catch { /* le journal ne doit jamais faire échouer le traitement */ }
    }
}
