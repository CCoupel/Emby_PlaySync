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
/// jamais — volontairement SANS trace, pour ne pas noyer le journal d'une entrée toutes les ~5 s par session active).
/// Sur <c>Stopped</c> : systématique (événement discret, pas un heartbeat). Dans les deux cas, garde D-c : aucune propagation
/// si le média est déjà lu pour le déclencheur À CET INSTANT (aucune marge de ratio position/durée — décision utilisateur v3,
/// voir <see cref="PlaybackPositionEngine"/>). Aucune garde anti-écho supplémentaire : nos écritures (<c>SetPosition</c>→
/// <c>SaveUserData</c>) ne déclenchent qu'un <c>UserDataSaved</c>, jamais un événement de session ; <see cref="PluginWriteTracker"/>
/// (déjà branché côté port) suffit. Aucune exception n'atteint le pipeline d'Emby.
/// <para>
/// Revue qa-integration-v031-20260927-202137-final.md (I28/I33) : au-delà d'une transition de pause bruyante (ci-dessus),
/// tout événement DISCRET reçu (arrêt, ou pause réelle une fois la transition détectée) qui ne produit AUCUNE propagation
/// laisse désormais une trace <c>Skipped</c> explicite (seuil trop court, position absente, échec de résolution de
/// l'utilisateur, ou garde D-c) — l'absence totale de trace serait en soi un défaut de diagnostic, même si la cause d'une
/// non-propagation donnée se révèle bénigne.
/// </para>
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
            // Événement mal formé (session/média absent) : pas assez d'information pour une entrée de journal utile
            // (userId/itemId inconnus) — silencieux, comme avant.
            if (!TryGetUserAndItem(e, out var userId, out var itemId)) return;
            // Chemin rapide en mémoire d'abord (comme PlayedTransitionTracker côté PlaybackListener) : un heartbeat de
            // lecture active sans changement de pause ne déclenche ni la garde D-c ni le moteur — VOLONTAIREMENT sans
            // trace (sinon une entrée toutes les ~5 s par session active, D-b).
            if (!PluginRuntime.PauseTransitions.OnProgress(userId, itemId, e.IsPaused)) return;
            TryPropagateOrJournal(userId, itemId, e.PlaybackPositionTicks);
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
            if (!TryGetUserAndItem(e, out var userId, out var itemId)) return;
            TryPropagateOrJournal(userId, itemId, e.PlaybackPositionTicks);
        }
        catch (Exception ex)
        {
            try { PluginRuntime.Log?.Error(LogFormat.FilePrefix + "erreur dans le gestionnaire PlaybackStopped", ex); } catch { /* jamais d'exception vers Emby */ }
        }
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
    /// Applique le seuil (D) puis la garde D-c ; propage sinon (le moteur journalise lui-même sa propre décision par
    /// playlist). Chaque sortie SANS propagation, au-delà de ce point (donc pour un événement discret, ou une pause dont
    /// la transition vient d'être détectée), laisse une trace <c>Skipped</c> explicite.
    /// </summary>
    private static void TryPropagateOrJournal(string userId, string itemId, long? positionTicks)
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
        // Garde D-c : état lu connu À CET INSTANT seulement, aucune marge de ratio position/durée (décision v3). Un arrêt à
        // 95-99 % avec IsPlayed=false à cet instant propage normalement (quitte à être écrasé par le lu dès qu'il arrive, #20).
        if (PluginRuntime.UserData?.IsPlayed(userId, itemId) == true)
        {
            // Nom distinct de "already-played" (R7, #20 : un MEMBRE déjà lu, par playlist) : ici c'est le DÉCLENCHEUR lui-même,
            // au niveau de l'événement (PlaylistId=null) — bloque toute tentative de propagation, pas un membre en particulier.
            JournalSkip(userId, itemId, "trigger-already-played");
            return;
        }
        PluginRuntime.PositionEngine?.Handle(userId, itemId, ticks);
    }

    private static void JournalSkip(string userId, string itemId, string reason)
    {
        var entry = JournalEntries.SkippedEntry(null, null, reason); // niveau événement (PlaylistId=null), comme "echo-consumed" côté PlaybackListener
        entry.UserId = userId;
        entry.ItemId = itemId;
        try { PluginRuntime.Journal?.Add(entry); } catch { /* le journal ne doit jamais faire échouer le traitement */ }
    }
}
