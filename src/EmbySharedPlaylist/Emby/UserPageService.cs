using EmbySharedPlaylist.UserPage;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace EmbySharedPlaylist.Emby;

/// <summary>
/// Endpoints <c>User/*</c> (D20, v1.1.0, #39) : première surface NON-ADMIN du plugin qui ÉCRIT des droits de partage
/// pour le compte du propriétaire. <c>[Authenticated]</c> SANS <c>Roles="Admin"</c> — confirmé accessible à un
/// compte non-admin par le spike U13 (question a, <c>_work/reports/spike-u13-verification-20260928-151423.md</c>).
///
/// <b>Identité</b> : TOUJOURS résolue côté serveur via <see cref="IAuthorizationContext.GetAuthorizationInfo"/>
/// (question d du spike U13, confirmé et déjà éprouvé par <c>Spike/SpikeU13Service.Whoami</c>) — non falsifiable,
/// aucun endpoint ci-dessous ne lit jamais un identifiant de DEMANDEUR depuis le body ou la query.
///
/// Toute la logique d'autorisation/orchestration vit dans <see cref="UserPlaylistService"/> (indépendant d'Emby,
/// testé sans SDK) ; cette classe ne fait QUE résoudre l'identité, appeler le service applicatif, et traduire le
/// résultat vers le statut HTTP + le corps d'erreur stables du contrat (<c>{ "Error": "&lt;code&gt;" }</c>) — jamais
/// de texte localisé ni de message d'exception ici (revue sécurité, § 5).
/// </summary>
public class UserPageService : IService, IRequiresRequest
{
    private readonly IAuthorizationContext _authContext;

    /// <summary>Injecté par le framework avant l'appel du handler (IRequiresRequest).</summary>
    public IRequest Request { get; set; } = null!;

    public UserPageService(ILibraryManager libraryManager, IUserManager userManager, IItemRepository itemRepository,
        IPlaylistManager playlistManager, IUserDataManager userDataManager, ILogManager logManager, IAuthorizationContext authContext)
    {
        // Même patron que DiagnosticsService/Spike.SpikeU13Service : idempotent, garantit PluginRuntime.* même si
        // aucun autre IService n'a encore été instancié par l'hôte.
        PluginRuntime.Initialize(libraryManager, userManager, itemRepository, playlistManager, userDataManager, logManager);
        _authContext = authContext;
    }

    public object Get(UserPlaylists request) => Respond(Service.ListOwned(RequesterId()));

    public object Get(UserUsers request) => Respond(Service.ListSelectableUsers(RequesterId()));

    public object Post(UserAddOrUpdateMember request) =>
        Respond(Service.AddOrUpdateMember(RequesterId(), request.PlaylistId, request.UserId, request.Level));

    public object Delete(UserRemoveMember request) =>
        Respond(Service.RemoveMember(RequesterId(), request.PlaylistId, request.UserId));

    public object Post(UserSetOption request) =>
        Respond(Service.SetOption(RequesterId(), request.PlaylistId, request.Family, request.Enabled));

    // ---- Aides ---------------------------------------------------------------------------------------------

    private static UserPlaylistService Service =>
        PluginRuntime.UserPlaylist ?? throw new InvalidOperationException("UserPlaylistService non initialisé");

    /// <summary>Identité du demandeur, TOUJOURS depuis la session (question d) — jamais un paramètre de requête.</summary>
    private string RequesterId()
    {
        var user = _authContext.GetAuthorizationInfo(Request).User;
        // Ne devrait jamais arriver sous [Authenticated] (utilisateur non résolu malgré une session valide) : 404
        // plutôt qu'une exception non gérée, cohérent avec « aucune fuite de message d'exception ».
        if (user == null) throw new ResourceNotFoundException("user");
        return user.Id.ToString("N");
    }

    /// <summary>Traduit un <see cref="UserPageResult{T}"/> en statut HTTP + corps stables. Succès : 200 + la valeur
    /// telle quelle (déjà à la forme du contrat). Échec : <see cref="StatusFor"/> + <c>{ "Error": "&lt;code&gt;" }</c>.</summary>
    private object Respond<T>(UserPageResult<T> result)
    {
        if (result.Ok) return result.Value!;
        Request.Response.StatusCode = StatusFor(result.Error!);
        return new UserErrorDto { Error = result.Error! };
    }

    private static int StatusFor(string error) => error switch
    {
        UserPageErrors.SharingDisabled => 403,
        UserPageErrors.NotFound => 404,
        UserPageErrors.InvalidLevel => 400,
        UserPageErrors.Self => 400,
        UserPageErrors.InvalidUser => 400,
        UserPageErrors.InvalidFamily => 400,
        UserPageErrors.NotShared => 409,
        UserPageErrors.Busy => 409,
        _ => 500 // UserPageErrors.Internal et tout code non anticipé : jamais de détail, cohérent avec le garde-fou de UserPlaylistService.
    };
}
