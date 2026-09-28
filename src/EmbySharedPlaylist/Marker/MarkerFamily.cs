namespace EmbySharedPlaylist.Marker;

/// <summary>
/// Famille d'étiquettes d'une playlist : deux familles indépendantes.
/// <c>RemoveSiLu</c> : retrait du média quand il passe à lu (actif en v0.2.0). <c>PropagerLu</c> : propagation de l'état
/// de lecture (posée et lue en NON dès v0.2.0, sans effet avant v0.3.0).
/// </summary>
public enum MarkerFamily
{
    RemoveSiLu,
    PropagerLu
}
