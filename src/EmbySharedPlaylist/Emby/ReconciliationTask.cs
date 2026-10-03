using EmbySharedPlaylist.Core;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace EmbySharedPlaylist.Emby;

/// <summary>
/// Tâche planifiée Emby « Emby Shared Playlist — réconciliation » : démarrage puis toutes les 5 minutes (la période est celle
/// du planificateur d'Emby, modifiable au tableau de bord, et la tâche se déclenche à la main par
/// <c>POST /ScheduledTasks/Running/{id}</c>). Pour chaque playlist gérée : première détection ou passe de grâce, sous le
/// verrou de la playlist. Les exceptions sont isolées par playlist (et jamais propagées à l'hôte).
/// </summary>
public sealed class ReconciliationTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly ILogManager _logManager;

    public ReconciliationTask(ILibraryManager libraryManager, IUserManager userManager, IItemRepository itemRepository,
        IPlaylistManager playlistManager, IProviderManager providerManager, IUserDataManager userDataManager, ILogManager logManager)
    {
        _logManager = logManager;
        PluginRuntime.Initialize(libraryManager, userManager, itemRepository, playlistManager, providerManager, userDataManager, logManager);
    }

    public string Name => "Emby Shared Playlist — réconciliation";
    public string Key => "EmbySharedPlaylistReconciliation";
    public string Description => "Pose les étiquettes remove-si-lu=NON, propager-lu=NON et propager-avancement=NON et le message d'aide sur les playlists partagées qui n'en ont pas.";
    public string Category => "Emby Shared Playlist";

    public bool IsHidden => false;
    public bool IsEnabled => true;
    public bool IsLogged => true;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo { Type = "StartupTrigger" },
        new TaskTriggerInfo { Type = "IntervalTrigger", IntervalTicks = TimeSpan.FromMinutes(5).Ticks }
    };

    public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
    {
        return Task.Run(() =>
        {
            // #26 (D-a) : réutilise cette tâche existante (démarrage + périodique) pour la passe de permission, sous
            // son propre try/catch isolé — une erreur sur l'un n'empêche jamais l'autre.
            try
            {
                var result = PluginRuntime.Reconciliation!.RunPass(cancellationToken);
                PluginRuntime.Log?.Info($"{LogFormat.FilePrefix}réconciliation terminée playlists={result.Playlists} posed={result.Posed} pending={result.Pending} durationMs={result.DurationMs}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                PluginRuntime.Log?.Error(LogFormat.FilePrefix + "réconciliation en erreur", ex);
            }

            try
            {
                var permission = PluginRuntime.AutoSharing!.RunPass(cancellationToken);
                PluginRuntime.Log?.Info($"{LogFormat.FilePrefix}permission terminée users={permission.Users} enabled={permission.Enabled} alreadyEnabled={permission.AlreadyEnabled} durationMs={permission.DurationMs}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                PluginRuntime.Log?.Error(LogFormat.FilePrefix + "permission en erreur", ex);
            }

            progress?.Report(100);
        }, cancellationToken);
    }
}
