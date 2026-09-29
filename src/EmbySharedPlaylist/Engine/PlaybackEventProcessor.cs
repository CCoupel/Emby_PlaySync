using System.Diagnostics;
using EmbySharedPlaylist.Core;

namespace EmbySharedPlaylist.Engine;

/// <summary>Issue du traitement d'un <c>UserDataSaved</c>.</summary>
public enum PlaybackEventOutcome
{
    /// <summary>Événement émis pendant une écriture du plugin (<see cref="WriteScope"/>) : ignoré (jamais de moteur), mais la mémoire de <see cref="PlayedTransitionTracker"/> est mise à jour (v1.2.0, S9f/R5).</summary>
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
/// garde anti-écho <see cref="PluginWriteTracker"/> EN PREMIER, puis <see cref="WriteScope"/> (repli), puis chemin rapide
/// en mémoire (tracker de transition), puis traitement immédiat (moteur) avec mesure de la durée. Ne capture aucune
/// exception : l'appelant (le listener) applique le try/catch total.
/// <para>
/// ORDRE CRITIQUE (revue A1, v0.3.0) : <c>EmbyUserDataGateway.MarkPlayed</c> (propagation, #20) enregistre l'écriture dans
/// <see cref="PluginWriteTracker"/> PUIS appelle <c>SaveUserData</c> sous <see cref="WriteScope"/> ; si le SDK émet
/// <c>UserDataSaved</c> de façon synchrone (même pile d'appel), cet écho arrive PENDANT le <see cref="WriteScope"/> de
/// l'écriture qui l'a causé. Si <see cref="WriteScope"/> était vérifié en premier, il intercepterait cet écho SANS jamais
/// consommer l'entrée du tracker, qui resterait « en attente » jusqu'à expiration (TTL 5 min) ou jusqu'à une action
/// RÉELLE ultérieure sur ce couple (mal classée en écho) — sous-déclenchement silencieux du retrait/de la propagation pour
/// les AUTRES playlists de ce membre pendant cette fenêtre. <see cref="PluginWriteTracker"/> est donc vérifié en premier
/// (il ne s'active QUE sur une écriture enregistrée par le plugin) ; <see cref="WriteScope"/> reste un repli défensif pour
/// une écriture hypothétique qui toucherait le flag lu sans passer par le tracker (aucune actuellement dans le code).
/// </para>
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
        if (_writeTracker.TryConsume(userId, itemId))
        {
            // Écho reconnu (AVANT WriteScope, voir le commentaire de classe) : la mémoire de transition doit rester à jour
            // pour ne pas fausser une vraie transition future, mais le moteur n'est JAMAIS appelé pour un écho (retour ignoré).
            _tracker.OnUserData(userId, itemId, saveReason, played);
            return PlaybackEventOutcome.Echo;
        }

        // Repli défensif : aucune écriture actuelle du plugin sur le flag lu ne devrait arriver ici sans être passée par
        // le tracker ci-dessus ; conservé au cas où une future écriture toucherait UserDataSaved sans s'y enregistrer.
        // v1.2.0 (S9f, R5) : un événement ignoré (ex. second UserDataSaved émis sur le fil d'une écriture du plugin, dont
        // l'écho unique a déjà été consommé) met quand même la mémoire de transition à jour : sinon un faux « non lu »
        // resterait mémorisé et une vraie action ultérieure (PlaybackFinished/Import avec played=true) serait classée
        // à tort en transition. Le résultat est volontairement ignoré : le moteur n'est JAMAIS appelé pour ce chemin.
        if (_writeScopeActive())
        {
            _tracker.OnUserData(userId, itemId, saveReason, played);
            return PlaybackEventOutcome.WriteScope;
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
