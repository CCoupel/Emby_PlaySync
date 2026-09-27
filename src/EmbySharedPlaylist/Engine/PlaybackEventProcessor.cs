using System.Diagnostics;
using EmbySharedPlaylist.Core;

namespace EmbySharedPlaylist.Engine;

/// <summary>Issue du traitement d'un <c>UserDataSaved</c>.</summary>
public enum PlaybackEventOutcome
{
    /// <summary>Événement émis pendant une écriture du plugin (<see cref="WriteScope"/>) : ignoré, rien n'est mémorisé.</summary>
    WriteScope,
    /// <summary>Pas de transition non lu → lu : retour immédiat, AUCUN accès base (ex. PlaybackProgress).</summary>
    NoTransition,
    /// <summary>Transition traitée par le moteur (durée enregistrée).</summary>
    Handled
}

/// <summary>
/// Logique pure du gestionnaire <c>UserDataSaved</c> (extraite de <c>PlaybackListener</c> pour être testée sans SDK) :
/// garde anti-écho (<see cref="WriteScope"/>) → chemin rapide en mémoire (tracker) → traitement immédiat (moteur) avec mesure
/// de la durée. Ne capture aucune exception : l'appelant (le listener) applique le try/catch total.
/// </summary>
public sealed class PlaybackEventProcessor
{
    private readonly PlayedTransitionTracker _tracker;
    private readonly Func<string, string, RemovalResult?> _handle;
    private readonly HandlerStats _stats;
    private readonly Func<bool> _writeScopeActive;

    public PlaybackEventProcessor(PlayedTransitionTracker tracker, Func<string, string, RemovalResult?> handle, HandlerStats stats,
        Func<bool>? writeScopeActive = null)
    {
        _tracker = tracker;
        _handle = handle;
        _stats = stats;
        _writeScopeActive = writeScopeActive ?? (() => WriteScope.Active);
    }

    public PlaybackEventOutcome Process(string userId, string itemId, string? saveReason, bool played)
    {
        if (_writeScopeActive()) return PlaybackEventOutcome.WriteScope;
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
