namespace EmbySharedPlaylist.Marker;

/// <summary>
/// Famille d'étiquettes d'une playlist : trois familles indépendantes à l'évaluation (v1.2.0, D21).
/// <c>RemoveSiLu</c> : retrait du média quand il passe à lu (effectif seulement si <c>PropagerLu</c> est aussi active).
/// <c>PropagerLu</c> : propagation du flag lu SEUL. <c>PropagerAvancement</c> : propagation de la position de lecture SEULE.
/// </summary>
public enum MarkerFamily
{
    RemoveSiLu,
    PropagerLu,
    PropagerAvancement
}
