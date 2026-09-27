using EmbySharedPlaylist.Core;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace EmbySharedPlaylist.Emby;

/// <summary>
/// Diagnostic permanent (#14) : <c>GET /SharedPlaylist/Diagnostics/Journal</c> et <c>/State</c>. Administrateur uniquement
/// (attribut sur chaque requête) ; 404 si <c>EnableDiagnostics</c> est faux ; lecture seule (sauf <c>clear</c>) ; ids seulement.
/// Les erreurs inattendues sont journalisées et renvoyées en 500 (type + message : acceptable
/// pour un endpoint admin de diagnostic, à ne pas reproduire dans un endpoint de production).
/// </summary>
public class DiagnosticsService : IService
{
    public DiagnosticsService(ILibraryManager libraryManager, IUserManager userManager, IItemRepository itemRepository,
        IPlaylistManager playlistManager, IUserDataManager userDataManager, ILogManager logManager)
    {
        PluginRuntime.Initialize(libraryManager, userManager, itemRepository, playlistManager, userDataManager, logManager);
    }

    public object Get(DiagnosticsJournal request) => Run(() =>
        DiagnosticsMapper.Journal(PluginRuntime.JournalStore.Snapshot(request.Clear), request.Kind));

    public object Get(DiagnosticsState request) => Run(() =>
        DiagnosticsMapper.State(PluginRuntime.Seen, PluginRuntime.Reconciliation?.LastPass, PluginRuntime.Handler.Snapshot(),
            Plugin.Instance?.Configuration.EffectiveGracePasses ?? 2, PluginRuntime.Skipped.Snapshot()));

    private static object Run(Func<object> action)
    {
        if (Plugin.Instance?.Configuration.EnableDiagnostics != true)
            throw new ResourceNotFoundException("Diagnostics disabled");
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is not ResourceNotFoundException)
        {
            PluginRuntime.Log?.Error(LogFormat.FilePrefix + "Diagnostics : erreur non gérée", ex);
            throw new InvalidOperationException($"error: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
