using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Engine;
using EmbySharedPlaylist.Reconciliation;
using EmbySharedPlaylist.UserPage;
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
    public static PauseTransitionTracker PauseTransitions { get; } = new();
    public static HandlerStats Handler { get; } = new();
    public static SkippedCounters Skipped { get; } = new();

    public static Log? Log { get; private set; }
    public static IJournal? Journal { get; private set; }
    public static IPlaylistGateway? Gateway { get; private set; }
    public static DefaultsService? Defaults { get; private set; }
    public static ReconciliationService? Reconciliation { get; private set; }
    public static FirstDetectionCoordinator? FirstDetection { get; private set; }
    public static ReadRemovalEngine? RemovalEngine { get; private set; }
    public static PlaybackPositionEngine? PositionEngine { get; private set; }
    public static PlaybackEventProcessor? PlaybackProcessor { get; private set; }
    public static IUserDataGateway? UserData { get; private set; }
    public static IUserPolicyGateway? PolicyGateway { get; private set; }
    public static AutoSharingService? AutoSharing { get; private set; }
    /// <summary>v1.1.0 (#39, D20) : page utilisateur — ports Emby dédiés (propriété/partages, annuaire).</summary>
    public static IShareGateway? ShareGateway { get; private set; }
    public static IUserDirectory? UserDirectory { get; private set; }
    /// <summary>v1.1.0 (#39, D20) : service applicatif composé pour les endpoints <c>User/*</c> (<c>Emby/UserPageService</c>).</summary>
    public static UserPlaylistService? UserPlaylist { get; private set; }

    /// <summary>Idempotent : le premier appel construit les services, les suivants renvoient.</summary>
    public static void Initialize(ILibraryManager libraryManager, IUserManager userManager, IItemRepository itemRepository,
        IPlaylistManager playlistManager, IUserDataManager userDataManager, ILogManager logManager)
    {
        lock (InitLock)
        {
            if (_initialized) return;

            var log = new Log(logManager.GetLogger("EmbySharedPlaylist"));
            // Les Skipped bruyants (already-seen, reentrant…) ne vont qu'aux compteurs ; le reste est journalisé (mémoire + logs).
            var journal = new AggregatingJournal(new LoggingJournal(JournalStore, log), Skipped, log);
            var clock = new SystemClock();
            var gateway = new EmbyPlaylistGateway(libraryManager, userManager, itemRepository, playlistManager, journal: journal);
            var userData = new EmbyUserDataGateway(userManager, libraryManager, userDataManager);
            var defaults = new DefaultsService(gateway, Seen, Locks, journal, HelpText.Message,
                () => Plugin.Instance?.Configuration.EffectiveGracePasses ?? 2, clock);

            Log = log;
            Journal = journal;
            Gateway = gateway;
            UserData = userData;
            Defaults = defaults;
            Reconciliation = new ReconciliationService(gateway, defaults, Seen, Locks, journal, clock);
            FirstDetection = new FirstDetectionCoordinator(gateway, defaults, Seen, journal, clock);
            RemovalEngine = new ReadRemovalEngine(gateway, userData, Tracker, defaults, Seen, Locks, journal, clock, budget: ReadRemovalEngine.DefaultBudget);
            var engine = RemovalEngine;
            PlaybackProcessor = new PlaybackEventProcessor(PlayedTransitions, Tracker, (u, i) => engine!.Handle(u, i), Handler);
            // Cousin de RemovalEngine, flux d'événements séparé (ISessionManager, pas UserDataSaved) : même verrou/budget par
            // playlist (constante partagée), branché depuis PlaybackSessionListener, jamais depuis PlaybackListener (#45).
            // Revue C1 : même HandlerStats partagée que PlaybackProcessor, pour que Diagnostics/State.Handler confonde les deux flux.
            PositionEngine = new PlaybackPositionEngine(gateway, userData, Tracker, defaults, Seen, Locks, journal, clock,
                budget: ReadRemovalEngine.DefaultBudget, handler: Handler);
            // #26 : port/service indépendants des playlists (aucun verrou/budget partagé, voir AutoSharingService).
            var policyGateway = new EmbyUserPolicyGateway(userManager);
            PolicyGateway = policyGateway;
            // Repli à faux (v1.0.0, GATE PROD, M1) : cohérent avec le nouveau défaut de PluginConfiguration si
            // Plugin.Instance est null (cas théorique, jamais observé en pratique).
            AutoSharing = new AutoSharingService(policyGateway, journal, clock, () => Plugin.Instance?.Configuration.AutoEnableSharing ?? false);

            // v1.1.0 (#39, D20) : page utilisateur — ports dédiés + service applicatif composé (indépendant d'Emby,
            // voir UserPage/UserPlaylistService.cs). Même verrou (Locks) et même DefaultsService que le moteur : le
            // premier partage depuis la page réutilise OnFirstDetection sous le même verrou réentrant (S10).
            var shareGateway = new EmbyShareGateway(libraryManager, userManager, itemRepository);
            ShareGateway = shareGateway;
            var userDirectory = new EmbyUserDirectory(userManager, policyGateway);
            UserDirectory = userDirectory;
            UserPlaylist = new UserPlaylistService(shareGateway, userDirectory, gateway, Locks, defaults, journal, clock);

            log.Info(LogFormat.Startup());
            _initialized = true;
        }
    }
}
