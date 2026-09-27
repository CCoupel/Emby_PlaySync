using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Engine;
using EmbySharedPlaylist.Reconciliation;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Logging;

namespace EmbySharedPlaylist.Emby;

/// <summary>
/// Racine de composition : les entrées Emby (tâche planifiée, écouteurs, services HTTP) sont instanciées séparément par
/// l'hôte ; elles partagent ici un seul jeu de singletons en mémoire (verrous, mémoire des playlists vues, journal).
/// Aucun état persisté : tout repart de zéro au redémarrage.
/// </summary>
public static class PluginRuntime
{
    private static readonly object InitLock = new();
    private static bool _initialized;

    public static EventJournal JournalStore { get; } = new();
    public static PluginWriteTracker Tracker { get; } = new();
    public static PlaylistLocks Locks { get; } = new();
    public static SeenPlaylists Seen { get; } = new();
    public static PlayedTransitionTracker PlayedTransitions { get; } = new();
    public static HandlerStats Handler { get; } = new();
    public static SkippedCounters Skipped { get; } = new();

    public static Log? Log { get; private set; }
    public static IJournal? Journal { get; private set; }
    public static IPlaylistGateway? Gateway { get; private set; }
    public static DefaultsService? Defaults { get; private set; }
    public static ReconciliationService? Reconciliation { get; private set; }
    public static FirstDetectionCoordinator? FirstDetection { get; private set; }
    public static ReadRemovalEngine? RemovalEngine { get; private set; }
    public static PlaybackEventProcessor? PlaybackProcessor { get; private set; }

    /// <summary>Idempotent : le premier appel construit les services, les suivants renvoient.</summary>
    public static void Initialize(ILibraryManager libraryManager, IUserManager userManager, IItemRepository itemRepository,
        IPlaylistManager playlistManager, ILogManager logManager)
    {
        lock (InitLock)
        {
            if (_initialized) return;

            var log = new Log(logManager.GetLogger("EmbySharedPlaylist"));
            // Les Skipped bruyants (already-seen, reentrant…) ne vont qu'aux compteurs ; le reste est journalisé (mémoire + logs).
            var journal = new AggregatingJournal(new LoggingJournal(JournalStore, log), Skipped, log);
            var clock = new SystemClock();
            var gateway = new EmbyPlaylistGateway(libraryManager, userManager, itemRepository, playlistManager, journal: journal);
            var defaults = new DefaultsService(gateway, Seen, Locks, journal, HelpText.Message,
                () => Plugin.Instance?.Configuration.EffectiveGracePasses ?? 2, clock);

            Log = log;
            Journal = journal;
            Gateway = gateway;
            Defaults = defaults;
            Reconciliation = new ReconciliationService(gateway, defaults, Seen, Locks, journal, clock);
            FirstDetection = new FirstDetectionCoordinator(gateway, defaults, Seen, journal, clock);
            RemovalEngine = new ReadRemovalEngine(gateway, defaults, Seen, Locks, journal, clock, budget: ReadRemovalEngine.DefaultBudget);
            var engine = RemovalEngine;
            PlaybackProcessor = new PlaybackEventProcessor(PlayedTransitions, (u, i) => engine!.Handle(u, i), Handler);
            log.Info(LogFormat.Startup());
            _initialized = true;
        }
    }
}
