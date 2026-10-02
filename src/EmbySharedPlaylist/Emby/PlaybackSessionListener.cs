using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Engine;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;

namespace EmbySharedPlaylist.Emby;

/// <summary>
/// Branche <see cref="PlaybackPositionEngine"/> sur <c>ISessionManager.PlaybackStart</c>/<c>.PlaybackProgress</c>/<c>.PlaybackStopped</c>
/// (#45, v1.2.1 D23) : flux D'ÉVÉNEMENTS SÉPARÉ de <see cref="PlaybackListener"/> (<c>UserDataSaved</c>, inchangé pour #12/#20/#21) —
/// U12 a établi que la pause n'est détectable que via la session (<c>PlaybackProgressEventArgs.IsPaused</c>).
/// <para>
/// <c>PlaybackStart</c> ouvre la session du couple (déclencheur, média) dans <see cref="PlaybackSyncTracker"/> et mémorise les
/// playlists cibles (elles servent encore à la fin de lecture si <c>remove-si-lu</c> a retiré le média). <c>Progress</c> : chaque
/// événement est classé par le tracker — transition de pause (immédiat), propagation périodique (au plus 1 / 10 s par couple,
/// verrous 250 ms, AUCUN journal : compteurs <c>Diagnostics/State.PositionProgress</c>), limité, ou ignoré (heartbeat en pause,
/// Progress tardif d'une session fermée). <c>Stopped</c> : systématique ; <c>PlayedToCompletion</c> (repli : UserData du
/// déclencheur lu + position 0 près de la fin) ⇒ fin de lecture (0 si <c>propager-lu=OUI</c>, sinon position brute).
/// Seuil : position ABSOLUE ≥ <see cref="MinPositionTicks"/> (~30 s), sauf remise à 0 de fin de lecture. Aucune garde
/// anti-écho supplémentaire : nos écritures (<c>SetPosition</c>→<c>SaveUserData</c>) ne déclenchent qu'un <c>UserDataSaved</c>,
/// jamais un événement de session ; <see cref="PluginWriteTracker"/> suffit. Aucune exception n'atteint le pipeline d'Emby.
/// </para>
/// <para>
/// Revue qa-integration-v031-20260927-202137-final.md (I28/I33) : tout événement DISCRET (pause, arrêt) qui ne produit
/// AUCUNE propagation laisse une trace <c>Skipped</c> explicite (seuil trop court, position absente).
/// </para>
/// </summary>
public sealed class PlaybackSessionListener : IServerEntryPoint
{
    /// <summary>Arrêts/pauses dont la POSITION ABSOLUE dans le média est en deçà de ce seuil sont ignorés (bruit, ~30 s).</summary>
    public static readonly long MinPositionTicks = PlaybackPositionEngine.MinPositionTicks;

    private readonly ISessionManager _sessionManager;

    public PlaybackSessionListener(ISessionManager sessionManager, IUserDataManager userDataManager, ILibraryManager libraryManager,
        IUserManager userManager, IItemRepository itemRepository, IPlaylistManager playlistManager, IProviderManager providerManager, ILogManager logManager)
    {
        _sessionManager = sessionManager;
        PluginRuntime.Initialize(libraryManager, userManager, itemRepository, playlistManager, providerManager, userDataManager, logManager);
    }

    public void Run()
    {
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
    }

    public void Dispose()
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
    }

    private static void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            if (!TryGetUserAndItem(e, out var userId, out var itemId)) return;
            var targets = PluginRuntime.PositionEngine?.ResolveTargets(userId, itemId);
            PluginRuntime.PlaybackSync.OnStart(userId, itemId, e.PlaySessionId, targets);
        }
        catch (Exception ex)
        {
            try { PluginRuntime.Log?.Error(LogFormat.FilePrefix + "erreur dans le gestionnaire PlaybackStart", ex); } catch { /* jamais d'exception vers Emby */ }
        }
    }

    private static void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            // Événement mal formé (session/média absent) : pas assez d'information pour une entrée de journal utile
            // (userId/itemId inconnus) — silencieux, comme avant.
            if (!TryGetUserAndItem(e, out var userId, out var itemId)) return;
            var sync = PluginRuntime.PlaybackSync;
            switch (sync.OnProgress(userId, itemId, e.PlaySessionId, e.IsPaused))
            {
                case PlaybackSyncDecision.Ignored:
                    return;
                case PlaybackSyncDecision.Throttled:
                    PluginRuntime.PositionProgress.IncrementThrottled(); // compteur seulement : jamais de trace par Progress
                    return;
                case PlaybackSyncDecision.Periodic:
                    // Bruit d'un Progress périodique (position absente / < 30 s, ex. Progress à 0 du démarrage) : silencieux.
                    if (e.PlaybackPositionTicks is not long periodicTicks || periodicTicks < MinPositionTicks) return;
                    Propagate(userId, itemId, periodicTicks, PositionTrigger.Periodic, PluginRuntime.PlaybackSync.GetFreshTargets(userId, itemId), e.PlaySessionId);
                    return;
                case PlaybackSyncDecision.PauseTransition:
                    TryPropagateOrJournal(userId, itemId, e.PlaybackPositionTicks, PositionTrigger.Pause, null);
                    return;
            }
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
            // Événement discret (pas un heartbeat périodique) : tentative systématique.
            if (!TryGetUserAndItem(e, out var userId, out var itemId)) return;
            var targets = PluginRuntime.PlaybackSync.OnStop(userId, itemId, e.PlaySessionId);
            var completed = e.PlayedToCompletion || LooksCompleted(userId, itemId, e);
            try
            {
                // Spike U15 : valeur réelle de PlayedToCompletion selon les clients.
                PluginRuntime.Log?.Debug(LogFormat.FilePrefix + $"PlaybackStopped user={userId} item={itemId} playedToCompletion={e.PlayedToCompletion} completed={completed} ticks={e.PlaybackPositionTicks}");
            }
            catch { /* log best effort */ }

            if (completed)
                Propagate(userId, itemId, e.PlaybackPositionTicks ?? 0, PositionTrigger.Completion, targets); // seuil géré par le moteur
            else
                TryPropagateOrJournal(userId, itemId, e.PlaybackPositionTicks, PositionTrigger.Stop, null);
        }
        catch (Exception ex)
        {
            try { PluginRuntime.Log?.Error(LogFormat.FilePrefix + "erreur dans le gestionnaire PlaybackStopped", ex); } catch { /* jamais d'exception vers Emby */ }
        }
    }

    /// <summary>
    /// Repli si <c>PlayedToCompletion</c> n'est pas fiable (U15) : Emby a déjà appliqué la fin de lecture au déclencheur
    /// (lu=vrai ET position remise à 0) et l'arrêt est près de la fin du média.
    /// </summary>
    private static bool LooksCompleted(string userId, string itemId, PlaybackStopEventArgs e)
    {
        try
        {
            var data = PluginRuntime.UserData;
            if (data == null) return false;
            return PlaybackCompletion.LooksCompleted(e.Item?.RunTimeTicks, e.PlaybackPositionTicks, data.IsPlayed(userId, itemId), data.GetPosition(userId, itemId));
        }
        catch { return false; }
    }

    private static bool TryGetUserAndItem(PlaybackProgressEventArgs e, out string userId, out string itemId)
    {
        userId = string.Empty;
        itemId = string.Empty;
        if (e.Session == null || e.Item == null) return false;
        if (!Guid.TryParse(e.Session.UserId, out var guid)) return false;
        userId = guid.ToString("N");
        itemId = e.Item.InternalId.ToString();
        return true;
    }

    /// <summary>
    /// Applique le seuil (D) ; propage sinon (le moteur journalise lui-même sa propre décision par playlist). Chaque sortie
    /// SANS propagation, au-delà de ce point (événement discret : pause, arrêt), laisse une trace <c>Skipped</c> explicite.
    /// </summary>
    private static void TryPropagateOrJournal(string userId, string itemId, long? positionTicks, PositionTrigger trigger, IReadOnlyCollection<string>? targets)
    {
        if (positionTicks is not long ticks)
        {
            JournalSkip(userId, itemId, "no-position");
            return;
        }
        if (ticks < MinPositionTicks)
        {
            JournalSkip(userId, itemId, "too-short");
            return;
        }
        Propagate(userId, itemId, ticks, trigger, targets);
    }

    /// <summary>Appelle le moteur puis met à jour le tracker (cibles mémorisées, minuterie de l'intervalle si aucun verrou occupé).</summary>
    private static void Propagate(string userId, string itemId, long ticks, PositionTrigger trigger, IReadOnlyCollection<string>? targets, string? playSessionId = null)
    {
        var sync = PluginRuntime.PlaybackSync;
        Func<bool>? isOpen = trigger == PositionTrigger.Periodic ? () => sync.IsOpen(userId, itemId, playSessionId) : null;
        var result = PluginRuntime.PositionEngine?.Handle(userId, itemId, ticks, trigger, targets, isOpen);
        if (result == null || trigger == PositionTrigger.Completion || trigger == PositionTrigger.Stop) return;
        // M1 : les cibles ne sont (re)datées que par une VRAIE résolution (targets == null) ; un Periodic qui réutilise les cibles
        // mémorisées ne les rafraîchit pas, sinon la péremption de 5 min ne jouerait jamais.
        if (targets == null && result.CandidatePlaylistIds != null) sync.SetTargets(userId, itemId, result.CandidatePlaylistIds);
        if (result.LockBusy == 0) PluginRuntime.PlaybackSync.MarkPropagated(userId, itemId, ticks);
    }

    private static void JournalSkip(string userId, string itemId, string reason)
    {
        var entry = JournalEntries.SkippedEntry(null, null, reason); // niveau événement (PlaylistId=null), comme "echo-consumed" côté PlaybackListener
        entry.UserId = userId;
        entry.ItemId = itemId;
        try { PluginRuntime.Journal?.Add(entry); } catch { /* le journal ne doit jamais faire échouer le traitement */ }
    }
}
