using EmbySharedPlaylist.Core;

namespace EmbySharedPlaylist.Reconciliation;

/// <summary>
/// Première détection « à l'action » (N1) : un événement d'ajout/retrait d'entrée ou de modification d'une playlist NON encore
/// vue déclenche la première détection (sous verrou, dans le gestionnaire). Jamais sur une playlist déjà vue (casserait le
/// remplacement de NON par OUI), jamais pendant une écriture du plugin (<see cref="WriteScope"/> : écho ignoré). Ne lève jamais.
/// </summary>
public sealed class FirstDetectionCoordinator
{
    private readonly IPlaylistGateway _gateway;
    private readonly DefaultsService _defaults;
    private readonly SeenPlaylists _seen;
    private readonly IJournal _journal;
    private readonly IClock? _clock;

    public FirstDetectionCoordinator(IPlaylistGateway gateway, DefaultsService defaults, SeenPlaylists seen, IJournal journal, IClock? clock = null)
    {
        _gateway = gateway;
        _defaults = defaults;
        _seen = seen;
        _journal = journal;
        _clock = clock;
    }

    public void OnPlaylistEvent(string playlistId)
    {
        try
        {
            if (WriteScope.Active) { _journal.Add(JournalEntries.SkippedEntry(_clock, playlistId, "reentrant")); return; }
            if (_seen.IsSeen(playlistId)) { _journal.Add(JournalEntries.SkippedEntry(_clock, playlistId, "already-seen")); return; }

            var snapshot = _gateway.Get(playlistId);
            if (snapshot == null) return;
            if (snapshot.OwnerId == null) { _journal.Add(JournalEntries.SkippedEntry(_clock, playlistId, "unknown-owner")); return; }
            if (!snapshot.IsShared) { _journal.Add(JournalEntries.SkippedEntry(_clock, playlistId, "not-shared")); return; }

            _defaults.OnFirstDetection(snapshot);
        }
        catch (Exception ex)
        {
            try { _journal.Add(JournalEntries.ErrorEntry(_clock, playlistId, ex)); } catch { /* jamais d'exception vers Emby */ }
        }
    }
}
