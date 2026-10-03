namespace EmbySharedPlaylist.Core;

/// <summary>F3 (#59) : issue d'une tentative de retrait d'une entrée.</summary>
public enum RemoveOutcome
{
    /// <summary>Le compte de la cible a baissé.</summary>
    Removed,
    /// <summary>Compte de la cible inchangé (identifiant d'entrée périmé : Emby a réécrit sans rien retirer pour la cible).</summary>
    NoEffect,
    /// <summary>Aucune entrée identifiable pour la cible.</summary>
    NotFound
}

/// <param name="Outcome">Issue.</param>
/// <param name="TargetRemaining">Entrées restantes de la cible après écriture ; −1 si inconnu (relecture non fiable ou passerelle sans cette information).</param>
/// <param name="OtherLost">Vrai si un autre média que la cible a perdu une entrée (déjà journalisé <c>wrong-entry</c>).</param>
public readonly record struct RemoveResult(RemoveOutcome Outcome, int TargetRemaining = -1, bool OtherLost = false);
