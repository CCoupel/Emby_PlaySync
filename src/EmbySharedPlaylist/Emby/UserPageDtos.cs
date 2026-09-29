using EmbySharedPlaylist.UserPage;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace EmbySharedPlaylist.Emby;

// Contrat : contracts/http-endpoints.md, section « Page utilisateur » (D20, v1.1.0, #39). Réponses en PascalCase
// (sérialiseur Emby). [Authenticated] SANS Roles="Admin" : accessible à tout compte authentifié (confirmé par le
// spike U13, question a, _work/reports/spike-u13-verification-20260928-151423.md) — la porte d'entrée réelle est
// Policy.AllowSharingPersonalItems, vérifiée par UserPlaylistService (403 sharing-disabled), pas le rôle.
//
// Les DTO de réponse RÉUTILISENT directement ceux d'EmbySharedPlaylist.UserPage (mêmes noms de champs PascalCase que
// le contrat : PlaylistId/Name/ItemCount/IsShared/Members/Options, UserId/Name pour les comptes sélectionnables) :
// aucune couche de correspondance supplémentaire, le service applicatif produit déjà la forme du contrat.

[Route("/SharedPlaylist/User/Playlists", "GET")]
[Authenticated]
public class UserPlaylists : IReturn<List<UserPagePlaylistDto>>
{
}

[Route("/SharedPlaylist/User/Playlists", "POST")]
[Authenticated]
public class UserCreatePlaylist : IReturn<UserPagePlaylistDto>
{
    public string? Name { get; set; }
}

[Route("/SharedPlaylist/User/Users", "GET")]
[Authenticated]
public class UserUsers : IReturn<List<UserPageSelectableDto>>
{
}

[Route("/SharedPlaylist/User/Playlists/{PlaylistId}/Members", "POST")]
[Authenticated]
public class UserAddOrUpdateMember : IReturn<UserPagePlaylistDto>
{
    public string PlaylistId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Level { get; set; } = string.Empty;
}

[Route("/SharedPlaylist/User/Playlists/{PlaylistId}/Members/{UserId}", "DELETE")]
[Authenticated]
public class UserRemoveMember : IReturn<UserPagePlaylistDto>
{
    public string PlaylistId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
}

[Route("/SharedPlaylist/User/Playlists/{PlaylistId}/Options", "POST")]
[Authenticated]
public class UserSetOption : IReturn<UserPagePlaylistDto>
{
    public string PlaylistId { get; set; } = string.Empty;
    public string Family { get; set; } = string.Empty;
    public bool Enabled { get; set; }
}

/// <summary>Corps d'erreur stable des endpoints <c>User/*</c> : <c>{ "Error": "&lt;code&gt;" }</c>, aucun texte
/// localisé ni message d'exception (contrat, § Page utilisateur).</summary>
public class UserErrorDto
{
    public string Error { get; set; } = string.Empty;
}
