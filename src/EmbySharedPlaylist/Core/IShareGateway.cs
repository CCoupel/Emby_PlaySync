namespace EmbySharedPlaylist.Core;

/// <summary>Un membre d'une playlist possédée (jamais le propriétaire — voir <see cref="OwnedPlaylist"/>).</summary>
public sealed record OwnedPlaylistMember(string UserId, string Level);

/// <summary>
/// Playlist possédée par le demandeur (page utilisateur, v1.1.0, #39) : partagée ou non. <see cref="Members"/>
/// EXCLUT toujours la ligne du propriétaire (cohérent avec le contrat <c>GET User/Playlists</c> : « Members exclut
/// le propriétaire »). <see cref="Tags"/> = étiquettes brutes de la playlist, pour évaluation par
/// <see cref="EmbySharedPlaylist.Marker.MarkerEvaluator"/> côté service applicatif (le port ne connaît pas la sémantique des familles).
/// </summary>
public sealed record OwnedPlaylist(
    string Id,
    string Name,
    int ItemCount,
    IReadOnlyList<OwnedPlaylistMember> Members,
    IReadOnlyList<string> Tags)
{
    /// <summary>Au moins un membre autre que le propriétaire (même définition que <see cref="PlaylistSnapshot.IsShared"/>).</summary>
    public bool IsShared => Members.Count > 0;
}

/// <summary>
/// Port des partages natifs Emby pour la page utilisateur (D20, v1.1.0, #39 — adaptateur : <c>Emby/EmbyShareGateway</c>,
/// Phase 2). Distinct d'<see cref="IPlaylistGateway"/> (moteur/réconciliation, tags+description+retrait) : ce port ne
/// s'occupe QUE de la propriété et des lignes de partage (<c>UserItemShare</c>). Propriété = ligne <c>ManageDelete</c>
/// du demandeur (ou propriétaire natif pour une playlist jamais encore partagée — mécanisme confirmé par le spike
/// U13, <c>_work/reports/spike-u13-20260928-144959.md</c> question c). Toute méthode qui prend un <c>ownerId</c>
/// vérifie la propriété SERVEUR : une playlist inexistante et une playlist existante mais non possédée sont
/// INDISCERNABLES (retour <c>null</c> dans les deux cas — anti-IDOR, CA6).
/// </summary>
public interface IShareGateway
{
    /// <summary>Playlists dont <paramref name="ownerId"/> est propriétaire, partagées ou non.</summary>
    IReadOnlyList<OwnedPlaylist> ListOwnedPlaylists(string ownerId);

    /// <summary>Lecture fraîche ; <c>null</c> si la playlist n'existe pas OU n'est pas possédée par <paramref name="ownerId"/>
    /// (même code, aucune distinction — anti-énumération).</summary>
    OwnedPlaylist? GetOwned(string ownerId, string playlistId);

    /// <summary>Ajoute ou change le niveau d'un membre (upsert). <paramref name="level"/> = valeur native Emby
    /// (<c>Read</c>/<c>Write</c> uniquement pour cet appelant — validé par le service applicatif AVANT l'appel, ce
    /// port ne revalide pas). Ne touche jamais la ligne du propriétaire.</summary>
    bool UpsertShare(string playlistId, string userId, string level);

    /// <summary>Retire le partage d'un membre. Faux si le membre n'existait pas.</summary>
    bool DeleteShare(string playlistId, string userId);
}
