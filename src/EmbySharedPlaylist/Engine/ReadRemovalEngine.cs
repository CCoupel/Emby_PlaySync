using System.Diagnostics;
using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Marker;
using EmbySharedPlaylist.Reconciliation;

namespace EmbySharedPlaylist.Engine;

/// <summary>Bilan d'un traitement de transition.</summary>
public sealed record RemovalResult(int Candidates, int PlaylistsChanged, int EntriesRemoved, int Skipped, long DurationMs);

/// <summary>
/// Retrait du média lu (#12) et propagation du flag lu (#20) : quand un utilisateur fait passer un média à lu, dans la
/// MÊME section critique par playlist gérée dont il est membre (propriétaire, <c>Write</c> ou <c>Read</c> : Q3) :
/// <list type="bullet">
/// <item><b>Retrait</b> (R4a, v1.2.0 D21 : subordonné à la propagation) SEULEMENT si <c>remove-si-lu=OUI</c> SEULE (NON
/// l'emporte) <b>et</b> <c>propager-lu=OUI</c> SEULE : retiré une entrée à la fois (résolue par ItemId à l'instant, plafond
/// 50, doublons compris). Si l'une des deux n'est pas active : rien (<c>Skipped inactive</c>), aucune étiquette modifiée.</item>
/// <item><b>Propagation</b> si <c>propager-lu=OUI</c> SEULE, indépendamment du retrait : pour chaque AUTRE membre sans le
/// flag lu et avec accès au média, pose <c>Played=true</c> (marque plugin, anti-écho #21). Membre déjà lu (R7) ou sans
/// accès (R8) : ignoré. « Non lu » n'est jamais propagé (R6, aucune transition détectée dans ce cas). Le flag lu SEUL est
/// propagé, jamais la position (famille <c>propager-avancement</c>, <see cref="PlaybackPositionEngine"/>). L'écriture chez
/// un membre est sérialisée par <see cref="UserItemLocks"/> (verrou (utilisateur, média), le plus interne).</item>
/// </list>
/// Traitement IMMÉDIAT, synchrone, dans le gestionnaire, sous le verrou de CHAQUE playlist, un seul à la fois (jamais deux) ;
/// une seule lecture fraîche des étiquettes pour les deux familles ; même budget global pour les deux. Première détection
/// si non vue (marquée « vue » avant d'écrire), état de <c>remove-si-lu</c> puis de <c>propager-lu</c> dans le journal (<c>MarkerSeen</c>). Le flag lu
/// du déclencheur n'est jamais modifié (R9) ; une playlist en erreur n'arrête pas les suivantes ; <see cref="Handle"/> ne
/// lève jamais.
/// </summary>
public sealed class ReadRemovalEngine
{
    public const int MaxEntriesPerPlaylist = 50;

    /// <summary>Budget cumulé du traitement d'une transition sur l'ensemble de ses playlists candidates (le gestionnaire tourne sur le fil de l'événement).</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(10);

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
    private readonly UserItemLocks _userItemLocks;

    public ReadRemovalEngine(IPlaylistGateway gateway, IUserDataGateway userData, PluginWriteTracker writeTracker,
        DefaultsService defaults, SeenPlaylists seen, PlaylistLocks locks, IJournal journal, IClock clock,
        TimeSpan? lockTimeout = null, TimeSpan? budget = null, UserItemLocks? userItemLocks = null)
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
        _budget = budget ?? DefaultBudget;
        _userItemLocks = userItemLocks ?? new UserItemLocks();
    }

    public RemovalResult Handle(string userId, string itemId)
    {
        var total = Stopwatch.StartNew();
        var candidates = 0;
        var changed = 0;
        var removed = 0;
        var skipped = 0;

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
                return new RemovalResult(0, 0, 0, 0, total.ElapsedMilliseconds);
            }

            candidates = playlists.Count;
            for (var index = 0; index < playlists.Count; index++)
            {
                var snapshot = playlists[index];
                // Budget global : au-delà, on s'arrête (le retrait est idempotent : la transition suivante ou une relecture reprendra).
                if (total.Elapsed >= _budget)
                {
                    skipped += playlists.Count - index;
                    Journal(JournalEntries.SkippedEntry(_clock, snapshot.Id, "budget-exceeded"));
                    break;
                }
                try
                {
                    var outcome = HandlePlaylist(snapshot, userId, itemId, total);
                    if (outcome < 0) skipped++;
                    else if (outcome > 0) { changed++; removed += outcome; }
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

        return new RemovalResult(candidates, changed, removed, skipped, total.ElapsedMilliseconds);
    }

    /// <returns>Nombre d'entrées retirées (&gt; 0), 0 si rien retiré, -1 si passée (verrou occupé).</returns>
    private int HandlePlaylist(PlaylistSnapshot snapshot, string userId, string itemId, Stopwatch total)
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

        // Relecture fraîche : les étiquettes sont lues à l'événement, jamais mises en cache. Sert aux deux familles.
        var fresh = _gateway.Get(snapshot.Id) ?? snapshot;
        var removeState = MarkerEvaluator.Evaluate(fresh.Tags, MarkerFamily.RemoveSiLu);
        var propagerState = MarkerEvaluator.Evaluate(fresh.Tags, MarkerFamily.PropagerLu);
        Journal(JournalEntries.Of(_clock, "MarkerSeen", snapshot.Id, $"family={MarkerEvaluator.FamilyName(MarkerFamily.RemoveSiLu)} state={removeState}"));
        // v1.2.0 (D21, R4a) : le retrait dépend de propager-lu ; son état est journalisé au même endroit.
        Journal(JournalEntries.Of(_clock, "MarkerSeen", snapshot.Id, $"family={MarkerEvaluator.FamilyName(MarkerFamily.PropagerLu)} state={propagerState}"));

        int result;
        if (removeState != MarkerState.Oui || propagerState != MarkerState.Oui)
        {
            // Contrat (http-endpoints.md, MarkerSeen) : « Skipped inactive si l'une des deux familles n'est pas Oui » — journalisé
            // même si la propagation du lu agit (Propagate journalise sa propre entrée). Ne pas restreindre (m3 annulée).
            Journal(JournalEntries.SkippedEntry(_clock, snapshot.Id, "inactive"));
            result = 0;
        }
        else
        {
            var count = 0;
            // Une entrée à la fois, résolue par ItemId à l'instant (les identifiants d'entrée ne sont pas stables).
            // v1.2.2 (#59, M1) : PAS de WriteScope englobant la boucle — la passerelle enveloppe chaque écriture ; un scope externe
            // resterait actif pendant l'attente de l'itération suivante et ferait compter « reentrant » l'ItemUpdated du worker Emby.
            while (count < MaxEntriesPerPlaylist && total.Elapsed < _budget && _gateway.RemoveOneEntry(snapshot.Id, itemId)) count++;

            if (count == 0)
            {
                Journal(JournalEntries.SkippedEntry(_clock, snapshot.Id, "already-removed"));
                result = 0;
            }
            else
            {
                var entry = JournalEntries.Of(_clock, "Removal", snapshot.Id, $"entries={count} durationMs={sw.ElapsedMilliseconds}");
                entry.UserId = userId;
                entry.ItemId = itemId;
                Journal(entry);
                result = count;
            }
        }

        // Propagation (#20) : famille indépendante, même snapshot/verrou, mêmes membres relus. Ne modifie jamais le
        // flag du déclencheur (déjà posé par Emby, hors plugin) ; aucune entrée si propager-lu n'est pas actif (D-d).
        if (propagerState == MarkerState.Oui)
            Propagate(fresh, userId, itemId, total);

        return result;
    }

    private void Propagate(PlaylistSnapshot snapshot, string userId, string itemId, Stopwatch total)
    {
        var members = snapshot.MemberIds;
        var propagated = 0;
        var alreadyPlayed = 0;
        var noAccess = 0;
        var lockBusy = 0;

        foreach (var memberId in members)
        {
            if (string.Equals(memberId, userId, StringComparison.Ordinal)) continue;
            if (total.Elapsed >= _budget) break; // budget global partagé avec le retrait : reprise à la transition suivante

            // Verrou (utilisateur, média) le plus interne (B7) : relecture, anti-écho et écriture dans la même section,
            // pour qu'une position propagée en parallèle chez ce membre ne soit ni écrasée ni perdue.
            using var userGate = _userItemLocks.TryAcquire(memberId, itemId, _lockTimeout);
            if (userGate == null)
            {
                lockBusy++;
                Journal(SkippedForMember(memberId, snapshot.Id, itemId, "lock-busy"));
                continue;
            }

            var isPlayed = _userData.IsPlayed(memberId, itemId);
            if (isPlayed == null)
            {
                noAccess++;
                Journal(SkippedForMember(memberId, snapshot.Id, itemId, "no-access")); // R8
                continue;
            }
            if (isPlayed == true)
            {
                alreadyPlayed++;
                Journal(SkippedForMember(memberId, snapshot.Id, itemId, "already-played")); // R7 : ni compteur ni date touchés
                continue;
            }

            _writeTracker.Register(memberId, itemId); // anti-écho (#21) AVANT l'écriture, sous le verrou
            var written = false;
            try { written = _userData.MarkPlayed(memberId, itemId); }
            finally { if (!written) _writeTracker.Unregister(memberId, itemId); } // pas d'écriture => pas d'écho attendu (m2)
            if (written) propagated++;
        }

        var detail = $"members={members.Count(m => !string.Equals(m, userId, StringComparison.Ordinal))} " +
                     $"propagated={propagated} alreadyPlayed={alreadyPlayed} noAccess={noAccess} lockBusy={lockBusy}";
        var entry = JournalEntries.Of(_clock, "Propagation", snapshot.Id, detail);
        entry.UserId = userId;
        entry.ItemId = itemId;
        Journal(entry);
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
