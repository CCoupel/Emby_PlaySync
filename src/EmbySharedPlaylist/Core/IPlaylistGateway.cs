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
    /// DE L'ÉCRITURE ; la description n'est écrite que si elle est vide à ce moment. Jamais de suppression.
    /// </summary>
    ApplyResult ApplyDefaults(string playlistId, IReadOnlyList<MarkerFamily> familiesToPose, string? overviewIfEmpty);
}
