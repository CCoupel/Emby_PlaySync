using EmbySharedPlaylist.Marker;

namespace EmbySharedPlaylist.Core;

/// <summary>Résultat d'<see cref="IPlaylistGateway.ApplyDefaults"/> : ce qui a réellement été écrit.</summary>
public sealed class ApplyResult
{
    public ApplyResult() { }

    public ApplyResult(IReadOnlyList<MarkerFamily> posed, bool overviewWritten)
    {
        Posed = posed;
        OverviewWritten = overviewWritten;
    }

    public IReadOnlyList<MarkerFamily> Posed { get; set; } = Array.Empty<MarkerFamily>();
    public bool OverviewWritten { get; set; }
}

/// <summary>
/// Instruction d'écriture de la description, ré-vérifiée AU MOMENT DE L'ÉCRITURE (comme les étiquettes) :
/// <see cref="RequiredCurrent"/> null = n'écrit <see cref="NewValue"/> QUE si la description est vide à cet instant (pose,
/// v0.2.0) ; non null = n'écrit QUE si la description est encore exactement égale à cette valeur (remplacement, #51, v0.3.0).
/// Le port ne connaît pas le contenu métier des textes : seule <c>Reconciliation/DefaultsService</c> sait qu'il s'agit de
/// <c>HelpText.V1</c>/<c>V2</c>.
/// </summary>
public sealed record OverviewChange(string? RequiredCurrent, string NewValue);

/// <summary>Port vers Emby (adaptateur : <c>Emby/EmbyPlaylistGateway</c>) : uniquement des appels internes du SDK, jamais de SQL.</summary>
public interface IPlaylistGateway
{
    /// <summary>Playlists gérées : propriétaire connu et au moins un membre explicite autre que lui (les publiques sans partage explicite sont ignorées).</summary>
    IReadOnlyList<PlaylistSnapshot> ListSharedPlaylists();

    /// <summary>Playlists gérées dont l'utilisateur est membre (propriétaire ou ligne de partage ≥ Read) et qui contiennent le média.</summary>
    IReadOnlyList<PlaylistSnapshot> ListSharedPlaylistsOfUserContaining(string userId, string itemId);

    /// <summary>Lecture fraîche ; null si la playlist n'existe pas.</summary>
    PlaylistSnapshot? Get(string playlistId);

    /// <summary>Retire UNE entrée du média (résolution par ItemId à l'instant) ; false si absent.</summary>
    bool RemoveOneEntry(string playlistId, string itemId);

    /// <summary>
    /// Une seule lecture-écriture : chaque étiquette n'est ajoutée que si aucune étiquette de sa famille n'existe AU MOMENT
    /// DE L'ÉCRITURE ; la description suit <paramref name="overview"/> (ré-vérifiée au même instant). Jamais de suppression.
    /// </summary>
    ApplyResult ApplyDefaults(string playlistId, IReadOnlyList<MarkerFamily> familiesToPose, OverviewChange? overview);
}
