using System.Diagnostics;
using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Marker;
using EmbySharedPlaylist.Reconciliation;

namespace EmbySharedPlaylist.Engine;

/// <summary>Bilan d'une propagation de position.</summary>
public sealed record PositionPropagationResult(int Candidates, int PlaylistsChanged, int Propagated, int Skipped, long DurationMs);

/// <summary>
/// Propagation de la position de lecture (#45), cousin de <see cref="ReadRemovalEngine"/> mais issu d'un flux d'événements
/// SÉPARÉ (<c>ISessionManager.PlaybackProgress</c>/<c>PlaybackStopped</c>, pas <c>UserDataSaved</c>) : classe distincte, pas
/// une fusion. Déclenché quand un utilisateur met en pause (transition détectée par <see cref="PauseTransitionTracker"/>) ou
/// arrête la lecture (systématique). Pour chaque playlist gérée dont il est membre et qui contient le média : première
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

    public PlaybackPositionEngine(IPlaylistGateway gateway, IUserDataGateway userData, PluginWriteTracker writeTracker,
        DefaultsService defaults, SeenPlaylists seen, PlaylistLocks locks, IJournal journal, IClock clock,
        TimeSpan? lockTimeout = null, TimeSpan? budget = null, HandlerStats? handler = null, UserItemLocks? userItemLocks = null)
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
    }

    /// <summary>
    /// Revue C1 : la durée est enregistrée dans <see cref="HandlerStats"/> (même instance partagée que
    /// <c>PlaybackEventProcessor</c> côté flux du lu) pour CHAQUE appel, quel que soit le chemin de sortie — le contrat
    /// (<c>Diagnostics/State.Handler</c>) annonce les deux flux d'événements confondus.
    /// </summary>
    public PositionPropagationResult Handle(string userId, string itemId, long ticks)
    {
        var total = Stopwatch.StartNew();
        var candidates = 0;
        var changed = 0;
        var propagated = 0;
        var skipped = 0;

        try
        {
            try
            {
                IReadOnlyList<PlaylistSnapshot> playlists;
                try
                {
                    playlists = _gateway.ListSharedPlaylistsOfUserContaining(userId, itemId);
                }
                catch (Exception ex)
                {
                    Journal(JournalEntries.ErrorEntry(_clock, null, ex));
                    return new PositionPropagationResult(0, 0, 0, 0, total.ElapsedMilliseconds);
                }

                candidates = playlists.Count;
                for (var index = 0; index < playlists.Count; index++)
                {
                    var snapshot = playlists[index];
                    if (total.Elapsed >= _budget)
                    {
                        skipped += playlists.Count - index;
                        Journal(JournalEntries.SkippedEntry(_clock, snapshot.Id, "budget-exceeded"));
                        break;
                    }
                    try
                    {
                        var outcome = HandlePlaylist(snapshot, userId, itemId, ticks, total);
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

            return new PositionPropagationResult(candidates, changed, propagated, skipped, total.ElapsedMilliseconds);
        }
        finally
        {
            _handler?.Record(total.ElapsedMilliseconds);
        }
    }

    /// <returns>Nombre de membres propagés (&gt; 0), 0 si rien propagé (inactif/déjà à cette position/personne), -1 si passée (verrou occupé).</returns>
    private int HandlePlaylist(PlaylistSnapshot snapshot, string userId, string itemId, long ticks, Stopwatch total)
    {
        var sw = Stopwatch.StartNew();
        using var gate = _locks.TryAcquire(snapshot.Id, _lockTimeout);
        if (gate == null)
        {
            Journal(JournalEntries.SkippedEntry(_clock, snapshot.Id, "lock-busy"));
            return -1;
        }

        // Première détection AVANT l'évaluation (pose des NON, message d'aide) ; verrou réentrant : même fil.
        if (!_seen.IsSeen(snapshot.Id)) _defaults.OnFirstDetection(snapshot);

        // Relecture fraîche : les étiquettes sont lues à l'événement, jamais mises en cache.
        var fresh = _gateway.Get(snapshot.Id) ?? snapshot;
        if (MarkerEvaluator.Evaluate(fresh.Tags, MarkerFamily.PropagerAvancement) != MarkerState.Oui)
        {
            Journal(JournalEntries.SkippedEntry(_clock, snapshot.Id, "inactive"));
            return 0;
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
            using var userGate = _userItemLocks.TryAcquire(memberId, itemId, _lockTimeout);
            if (userGate == null)
            {
                lockBusy++;
                Journal(SkippedForMember(memberId, snapshot.Id, itemId, "lock-busy"));
                continue;
            }

            if (!_userData.HasAccess(memberId, itemId))
            {
                noAccess++;
                Journal(SkippedForMember(memberId, snapshot.Id, itemId, "no-access")); // R8
                continue;
            }

            // Vérifié AVANT d'enregistrer l'anti-écho : n'enregistrer que si une écriture réelle va suivre (sinon
            // l'entrée du tracker resterait "pending" sans jamais être consommée — même piège que la revue A1 sur #20).
            if (_userData.GetPosition(memberId, itemId) == ticks)
            {
                samePosition++;
                Journal(SkippedForMember(memberId, snapshot.Id, itemId, "same-position"));
                continue;
            }

            _writeTracker.Register(memberId, itemId);
            var written = false;
            try { written = _userData.SetPosition(memberId, itemId, ticks); }
            finally { if (!written) _writeTracker.Unregister(memberId, itemId); } // pas d'écriture => pas d'écho attendu (m2)
            if (written) propagated++;
            else samePosition++; // résiduel : position redevenue identique entre notre lecture et l'écriture (rare)
        }

        var detail = $"members={fresh.MemberIds.Count(m => !string.Equals(m, userId, StringComparison.Ordinal))} " +
                     $"propagated={propagated} samePosition={samePosition} noAccess={noAccess} lockBusy={lockBusy} durationMs={sw.ElapsedMilliseconds}";
        var entry = JournalEntries.Of(_clock, "PositionPropagation", snapshot.Id, detail);
        entry.UserId = userId;
        entry.ItemId = itemId;
        Journal(entry);
        return propagated;
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
