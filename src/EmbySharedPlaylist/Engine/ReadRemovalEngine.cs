using System.Diagnostics;
using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Marker;
using EmbySharedPlaylist.Reconciliation;

namespace EmbySharedPlaylist.Engine;

/// <summary>Bilan d'un traitement de transition.</summary>
public sealed record RemovalResult(int Candidates, int PlaylistsChanged, int EntriesRemoved, int Skipped, long DurationMs);

/// <summary>
/// Retrait du média lu (#12) : quand un utilisateur fait passer un média à lu, il est retiré de chaque playlist gérée dont
/// il est membre (propriétaire, <c>Write</c> ou <c>Read</c> : Q3) qui porte <c>remove-si-lu=OUI</c> SEULE (NON l'emporte ;
/// <c>propager-lu</c> n'est jamais consulté). Traitement IMMÉDIAT, synchrone, dans le gestionnaire, sous le verrou de
/// CHAQUE playlist, un seul à la fois (jamais deux). Par playlist : première détection si non vue (marquée « vue » avant
/// d'écrire), relecture fraîche, état lu dans le journal (<c>MarkerSeen</c>), puis retrait une entrée à la fois (résolue
/// par ItemId à l'instant, plafond 50, doublons compris). Le flag lu n'est jamais modifié (R9). Une playlist en erreur
/// n'arrête pas les suivantes ; <see cref="Handle"/> ne lève jamais. Suspendu quand <c>isSuspended</c> est vrai.
/// </summary>
public sealed class ReadRemovalEngine
{
    public const int MaxEntriesPerPlaylist = 50;

    /// <summary>Budget cumulé du traitement d'une transition sur l'ensemble de ses playlists candidates (le gestionnaire tourne sur le fil de l'événement).</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(10);

    private readonly IPlaylistGateway _gateway;
    private readonly DefaultsService _defaults;
    private readonly SeenPlaylists _seen;
    private readonly PlaylistLocks _locks;
    private readonly IJournal _journal;
    private readonly IClock _clock;
    private readonly TimeSpan _lockTimeout;
    private readonly Func<bool> _isSuspended;
    private readonly TimeSpan _budget;

    public ReadRemovalEngine(IPlaylistGateway gateway, DefaultsService defaults, SeenPlaylists seen, PlaylistLocks locks,
        IJournal journal, IClock clock, TimeSpan? lockTimeout = null, Func<bool>? isSuspended = null, TimeSpan? budget = null)
    {
        _gateway = gateway;
        _defaults = defaults;
        _seen = seen;
        _locks = locks;
        _journal = journal;
        _clock = clock;
        _lockTimeout = lockTimeout ?? PlaylistLocks.DefaultTimeout;
        _isSuspended = isSuspended ?? (() => false);
        _budget = budget ?? DefaultBudget;
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
            if (_isSuspended()) return new RemovalResult(0, 0, 0, 0, 0);

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

        // Relecture fraîche : les étiquettes sont lues à l'événement, jamais mises en cache.
        var fresh = _gateway.Get(snapshot.Id) ?? snapshot;
        var state = MarkerEvaluator.Evaluate(fresh.Tags, MarkerFamily.RemoveSiLu);
        Journal(JournalEntries.Of(_clock, "MarkerSeen", snapshot.Id, $"family={MarkerEvaluator.FamilyName(MarkerFamily.RemoveSiLu)} state={state}"));

        if (state != MarkerState.Oui)
        {
            Journal(JournalEntries.SkippedEntry(_clock, snapshot.Id, "inactive"));
            return 0;
        }

        var count = 0;
        using (WriteScope.Enter())
        {
            // Une entrée à la fois, résolue par ItemId à l'instant (les identifiants d'entrée ne sont pas stables).
            while (count < MaxEntriesPerPlaylist && total.Elapsed < _budget && _gateway.RemoveOneEntry(snapshot.Id, itemId)) count++;
        }

        if (count == 0)
        {
            Journal(JournalEntries.SkippedEntry(_clock, snapshot.Id, "already-removed"));
            return 0;
        }

        var entry = JournalEntries.Of(_clock, "Removal", snapshot.Id, $"entries={count} durationMs={sw.ElapsedMilliseconds}");
        entry.UserId = userId;
        entry.ItemId = itemId;
        Journal(entry);
        return count;
    }

    private void Journal(JournalEntry entry)
    {
        try { _journal.Add(entry); } catch { /* le journal ne doit jamais faire échouer le traitement */ }
    }
}
