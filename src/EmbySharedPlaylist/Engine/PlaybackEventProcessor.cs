using System.Diagnostics;
using EmbySharedPlaylist.Core;

namespace EmbySharedPlaylist.Engine;

/// <summary>Issue du traitement d'un <c>UserDataSaved</c>.</summary>
public enum PlaybackEventOutcome
{
    /// <summary>Événement émis pendant une écriture du plugin (<see cref="WriteScope"/>) : ignoré, rien n'est mémorisé.</summary>
    WriteScope,
    /// <summary>Écho reconnu par <see cref="PluginWriteTracker"/> (écriture du plugin, éventuellement sur un autre fil) :
    /// ignoré, mais la mémoire de <see cref="PlayedTransitionTracker"/> est quand même mise à jour (garde S6a-c, R5).</summary>
    Echo,
    /// <summary>Pas de transition non lu → lu : retour immédiat, AUCUN accès base (ex. PlaybackProgress).</summary>
    NoTransition,
    /// <summary>Transition traitée par le moteur (durée enregistrée).</summary>
    Handled
}

/// <summary>
/// Logique pure du gestionnaire <c>UserDataSaved</c> (extraite de <c>PlaybackListener</c> pour être testée sans SDK) :
/// garde anti-écho <see cref="WriteScope"/> (même fil) → garde anti-écho <see cref="PluginWriteTracker"/> (garde PRINCIPALE
/// pour une écriture ciblant un autre utilisateur, dont l'événement retour peut arriver sur un autre fil : sans elle, l'écho
/// d'une propagation chez un membre de plusieurs playlists redéclencherait le moteur pour ses AUTRES playlists, violant
/// l'absence de transitivité R5/S6a-c) → chemin rapide en mémoire (tracker de transition) → traitement immédiat (moteur)
/// avec mesure de la durée. Ne capture aucune exception : l'appelant (le listener) applique le try/catch total.
/// </summary>
public sealed class PlaybackEventProcessor
{
    private readonly PlayedTransitionTracker _tracker;
    private readonly PluginWriteTracker _writeTracker;
    private readonly Func<string, string, RemovalResult?> _handle;
    private readonly HandlerStats _stats;
    private readonly Func<bool> _writeScopeActive;

    public PlaybackEventProcessor(PlayedTransitionTracker tracker, PluginWriteTracker writeTracker,
        Func<string, string, RemovalResult?> handle, HandlerStats stats, Func<bool>? writeScopeActive = null)
    {
        _tracker = tracker;
        _writeTracker = writeTracker;
        _handle = handle;
        _stats = stats;
        _writeScopeActive = writeScopeActive ?? (() => WriteScope.Active);
    }

    public PlaybackEventOutcome Process(string userId, string itemId, string? saveReason, bool played)
    {
        if (_writeScopeActive()) return PlaybackEventOutcome.WriteScope;

        if (_writeTracker.TryConsume(userId, itemId))
        {
            // Écho reconnu : la mémoire de transition doit rester à jour pour ne pas fausser une vraie transition future,
            // mais le moteur n'est JAMAIS appelé pour un écho (valeur de retour ignorée intentionnellement).
            _tracker.OnUserData(userId, itemId, saveReason, played);
            return PlaybackEventOutcome.Echo;
        }

        if (!_tracker.OnUserData(userId, itemId, saveReason, played)) return PlaybackEventOutcome.NoTransition;

        var sw = Stopwatch.StartNew();
        try
        {
            _handle(userId, itemId);
        }
        finally
        {
            _stats.Record(sw.ElapsedMilliseconds);
        }
        return PlaybackEventOutcome.Handled;
    }
}
