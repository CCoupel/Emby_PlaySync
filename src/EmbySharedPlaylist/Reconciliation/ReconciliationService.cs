using System.Diagnostics;
using EmbySharedPlaylist.Core;

namespace EmbySharedPlaylist.Reconciliation;

/// <summary>Bilan d'une passe. <c>Playlists</c> = playlists examinées (le port ne liste que les gérées : égal à <c>Shared</c>).</summary>
public sealed record PassResult(int Playlists, int Shared, int Posed, int Pending, long DurationMs);

/// <summary>Dernière passe, pour Diagnostics/State.</summary>
public sealed record LastPassInfo(DateTimeOffset Ts, long DurationMs, int PlaylistsSeen, int SharedManaged);

/// <summary>
/// Passe de réconciliation : pour chaque playlist gérée, première détection (id inconnu) ou passe de grâce. Sert à ce
/// qu'aucun événement ne signale (partage créé sans action ensuite ; repose d'une étiquette absente). Prend le verrou de
/// CHAQUE playlist (jamais deux à la fois, pas de blocage : délai dépassé = <c>Skipped lock-busy</c>) ; les exceptions sont
/// isolées par playlist ; idempotente.
/// </summary>
public sealed class ReconciliationService
{
    private readonly IPlaylistGateway _gateway;
    private readonly DefaultsService _defaults;
    private readonly SeenPlaylists _seen;
    private readonly PlaylistLocks _locks;
    private readonly IJournal _journal;
    private readonly IClock _clock;
    private readonly TimeSpan _lockTimeout;
    private LastPassInfo? _last;

    public ReconciliationService(IPlaylistGateway gateway, DefaultsService defaults, SeenPlaylists seen, PlaylistLocks locks, IJournal journal, IClock clock,
        TimeSpan? lockTimeout = null)
    {
        _gateway = gateway;
        _defaults = defaults;
        _seen = seen;
        _locks = locks;
        _journal = journal;
        _clock = clock;
        _lockTimeout = lockTimeout ?? PlaylistLocks.DefaultTimeout;
    }

    public LastPassInfo? LastPass => Volatile.Read(ref _last);

    public PassResult RunPass(CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var shared = 0;
        var posed = 0;
        var pending = 0;

        IReadOnlyList<PlaylistSnapshot> playlists;
        try
        {
            playlists = _gateway.ListSharedPlaylists();
        }
        catch (Exception ex)
        {
            _journal.Add(JournalEntries.ErrorEntry(_clock, null, ex));
            playlists = Array.Empty<PlaylistSnapshot>();
        }

        foreach (var p in playlists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            shared++;
            try
            {
                using var gate = _locks.TryAcquire(p.Id, _lockTimeout);
                if (gate == null)
                {
                    pending++;
                    _journal.Add(JournalEntries.SkippedEntry(_clock, p.Id, "lock-busy"));
                    continue;
                }
                var outcome = _seen.IsSeen(p.Id) ? _defaults.OnPass(p) : _defaults.OnFirstDetection(p);
                posed += outcome.MarkersPosed;
                if (outcome.PendingGrace > 0) pending++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _journal.Add(JournalEntries.ErrorEntry(_clock, p.Id, ex));
            }
        }

        sw.Stop();
        var result = new PassResult(shared, shared, posed, pending, sw.ElapsedMilliseconds);
        Volatile.Write(ref _last, new LastPassInfo(_clock.UtcNow, result.DurationMs, result.Playlists, result.Shared));
        _journal.Add(JournalEntries.Of(_clock, JournalEntries.ScanPass, null,
            $"playlists={result.Playlists} shared={result.Shared} posed={result.Posed} pending={result.Pending} durationMs={result.DurationMs}"));
        return result;
    }
}
