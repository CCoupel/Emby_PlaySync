using EmbySharedPlaylist.Core;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;

namespace EmbySharedPlaylist.Emby;

/// <summary>
/// Branche <see cref="PlaybackPositionEngine"/> sur <c>ISessionManager.PlaybackProgress</c>/<c>.PlaybackStopped</c> (#45) : flux
/// D'ÉVÉNEMENTS SÉPARÉ de <see cref="PlaybackListener"/> (<c>UserDataSaved</c>, inchangé pour #12/#20/#21) — U12 a établi que la
/// pause n'est détectable que via la session (<c>PlaybackProgressEventArgs.IsPaused</c>), pas via les données utilisateur.
/// <c>PlaybackStart</c> n'est pas écouté (rien à propager au démarrage). Un arrêt court (moins de <see cref="MinPositionTicks"/>,
/// ~30 s) est ignoré (bruit d'un essai immédiatement interrompu). Sur <c>Progress</c> : seule la transition
/// <c>IsPaused</c> faux/inconnu→vrai déclenche (<see cref="PauseTransitionTracker"/>, un heartbeat périodique sans pause n'écrit
/// jamais). Sur <c>Stopped</c> : systématique (événement discret, pas un heartbeat). Dans les deux cas, garde D-c : aucune
/// propagation si le média est déjà lu pour le déclencheur À CET INSTANT (aucune marge de ratio position/durée — décision
/// utilisateur v3, voir <see cref="PlaybackPositionEngine"/>). Aucune garde anti-écho supplémentaire : nos écritures
/// (<c>SetPosition</c>→<c>SaveUserData</c>) ne déclenchent qu'un <c>UserDataSaved</c>, jamais un événement de session ;
/// <see cref="PluginWriteTracker"/> (déjà branché côté port) suffit. Aucune exception n'atteint le pipeline d'Emby.
/// </summary>
public sealed class PlaybackSessionListener : IServerEntryPoint
{
    /// <summary>Arrêts/pauses en deçà de ce seuil sont ignorés (bruit, ~30 s).</summary>
    public static readonly long MinPositionTicks = TimeSpan.FromSeconds(30).Ticks;

    private readonly ISessionManager _sessionManager;

    public PlaybackSessionListener(ISessionManager sessionManager, IUserDataManager userDataManager, ILibraryManager libraryManager,
        IUserManager userManager, IItemRepository itemRepository, IPlaylistManager playlistManager, ILogManager logManager)
    {
        _sessionManager = sessionManager;
        PluginRuntime.Initialize(libraryManager, userManager, itemRepository, playlistManager, userDataManager, logManager);
    }

    public void Run()
    {
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
    }

    public void Dispose()
    {
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
    }

    private static void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            if (!TryExtract(e, out var userId, out var itemId, out var ticks)) return;
            // Chemin rapide en mémoire d'abord (comme PlayedTransitionTracker côté PlaybackListener) : un heartbeat de lecture
            // active sans changement de pause ne déclenche ni la garde D-c ni le moteur.
            if (!PluginRuntime.PauseTransitions.OnProgress(userId, itemId, e.IsPaused)) return;
            TryPropagate(userId, itemId, ticks);
        }
        catch (Exception ex)
        {
            try { PluginRuntime.Log?.Error(LogFormat.FilePrefix + "erreur dans le gestionnaire PlaybackProgress", ex); } catch { /* jamais d'exception vers Emby */ }
        }
    }

    private static void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        try
        {
            // Événement discret (pas un heartbeat périodique) : pas de tracker, tentative systématique sous réserve de D-c.
            if (!TryExtract(e, out var userId, out var itemId, out var ticks)) return;
            TryPropagate(userId, itemId, ticks);
        }
        catch (Exception ex)
        {
            try { PluginRuntime.Log?.Error(LogFormat.FilePrefix + "erreur dans le gestionnaire PlaybackStopped", ex); } catch { /* jamais d'exception vers Emby */ }
        }
    }

    private static bool TryExtract(PlaybackProgressEventArgs e, out string userId, out string itemId, out long ticks)
    {
        userId = string.Empty;
        itemId = string.Empty;
        ticks = 0;
        if (e.Session == null || e.Item == null || e.PlaybackPositionTicks is not long raw) return false;
        if (raw < MinPositionTicks) return false; // arrêt/pause court ignoré (bruit)
        if (!Guid.TryParse(e.Session.UserId, out var guid)) return false;
        userId = guid.ToString("N");
        itemId = e.Item.InternalId.ToString();
        ticks = raw;
        return true;
    }

    private static void TryPropagate(string userId, string itemId, long ticks)
    {
        // Garde D-c : état lu connu À CET INSTANT seulement, aucune marge de ratio position/durée (décision v3). Un arrêt à
        // 95-99 % avec IsPlayed=false à cet instant propage normalement (quitte à être écrasé par le lu dès qu'il arrive, #20).
        if (PluginRuntime.UserData?.IsPlayed(userId, itemId) == true) return;
        PluginRuntime.PositionEngine?.Handle(userId, itemId, ticks);
    }
}
