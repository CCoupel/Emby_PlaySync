namespace EmbySharedPlaylist.Marker;

/// <summary>État d'une famille d'étiquettes sur une playlist. <c>Oui</c> = OUI seule = actif.</summary>
public enum MarkerState
{
    /// <summary>Aucune étiquette de la famille (traité comme NON).</summary>
    None,
    /// <summary><c>=NON</c> seule.</summary>
    Non,
    /// <summary><c>=OUI</c> seule : actif.</summary>
    Oui,
    /// <summary><c>=OUI</c> et <c>=NON</c> ensemble : NON l'emporte, inactif.</summary>
    Both
}
