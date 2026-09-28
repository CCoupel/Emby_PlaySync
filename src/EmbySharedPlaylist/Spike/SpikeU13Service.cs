using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Emby;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace EmbySharedPlaylist.Spike;

/// <summary>
/// SONDE TEMPORAIRE U13 (#39, branche <c>spike/u13</c>, JAMAIS mergée dans <c>milestone/v1.1.0</c> — même règle que
/// les sondes <c>Spike/*</c> de v0.1.0, retirées avant merge). Plan : <c>_work/reports/plan-20260928-143007.md</c>
/// Phase 0. Répond aux questions a/c/d/e/f (b : voir le commentaire dans <see cref="Plugin.GetPages"/> et le rapport
/// de spike — aucun appel serveur nécessaire, c'est une question de mécanisme disponible).
///
/// 404 si <see cref="PluginConfiguration.EnableSpikeU13"/> est faux. [Authenticated] SANS <c>Roles="Admin"</c> :
/// c'est précisément ce qui est sondé (question a — contrairement à <see cref="DiagnosticsService"/>).
///
/// Gardes strictes (aucune donnée réelle touchée) :
/// <list type="bullet">
/// <item>Toute playlist manipulée doit avoir un nom commençant PAR <c>SPIKE-U13-</c> (comparaison ordinale).</item>
/// <item>Tout compte CIBLE (écriture de partage) doit avoir un nom commençant par <c>test_</c>.</item>
/// </list>
/// Aucune garde sur le DEMANDEUR : la sonde répond volontairement à tout compte authentifié (question a), y compris
/// non-<c>test_*</c> — c'est lui qui est sondé, pas la cible.
/// </summary>
public class SpikeU13Service : IService, IRequiresRequest
{
    private const string PlaylistPrefix = "SPIKE-U13-";
    private const string TestUserPrefix = "test_";

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IItemRepository _itemRepository;
    private readonly IAuthorizationContext _authContext;

    /// <summary>Injecté par le framework avant l'appel du handler (IRequiresRequest).</summary>
    public IRequest Request { get; set; } = null!;

    public SpikeU13Service(ILibraryManager libraryManager, IUserManager userManager, IItemRepository itemRepository,
        IPlaylistManager playlistManager, IUserDataManager userDataManager, ILogManager logManager, IAuthorizationContext authContext)
    {
        // Même patron que DiagnosticsService : idempotent, garantit PluginRuntime.Log même si aucun autre IService
        // n'a encore été instancié par l'hôte.
        PluginRuntime.Initialize(libraryManager, userManager, itemRepository, playlistManager, userDataManager, logManager);
        _libraryManager = libraryManager;
        _userManager = userManager;
        _itemRepository = itemRepository;
        _authContext = authContext;
    }

    // ---- d : identité du demandeur -------------------------------------------------------------------------

    public object Get(SpikeU13Whoami request) => Run(() =>
    {
        var info = _authContext.GetAuthorizationInfo(Request);
        var user = info.User;
        return new SpikeU13WhoamiDto
        {
            UserId = user?.Id.ToString("N"),
            UserName = user?.Name,
            IsAdmin = user != null && _userManager.GetUserPolicy(user).IsAdministrator,
            Client = info.Client,
            Device = info.Device
        };
    });

    // ---- a / c : lecture brute des lignes de partage -------------------------------------------------------

    public object Get(SpikeU13Shares request) => Run(() => ReadShares(RequirePlaylistName(request.PlaylistName)));

    // ---- e / f : écriture d'une ligne de partage -----------------------------------------------------------

    public object Post(SpikeU13Share request) => Run(() =>
    {
        var playlistName = RequirePlaylistName(request.PlaylistName);
        var target = RequireTestUser(request.TargetUserId);
        if (!Enum.TryParse<UserItemShareLevel>(request.Level, ignoreCase: true, out var level))
            throw new ArgumentException($"Level invalide : '{request.Level}' (attendu Read|Write|Manage|ManageDelete)");

        var before = ReadShares(playlistName);
        if (!before.PlaylistFound || before.PlaylistId == null)
            throw new ArgumentException($"Playlist introuvable : '{playlistName}'");

        var itemId = long.Parse(before.PlaylistId);
        using (WriteScope.Enter())
        {
            // e : SaveUserItemShares prend un TABLEAU (pas de paramètre itemId séparé) — une seule ligne ici pour
            // observer si les lignes PRÉ-EXISTANTES de cet item (propriétaire compris) survivent ou disparaissent.
            _itemRepository.SaveUserItemShares(new[]
            {
                new UserItemShare { ItemId = itemId, UserId = target.InternalId, ShareLevel = level }
            });
        }

        return new SpikeU13ShareResultDto { Before = before, After = ReadShares(playlistName) };
    });

    // ---- e : suppression par seuil (PAS par userId — voir signature) --------------------------------------

    public object Post(SpikeU13DeleteShares request) => Run(() =>
    {
        var playlistName = RequirePlaylistName(request.PlaylistName);
        var before = ReadShares(playlistName);
        if (!before.PlaylistFound || before.PlaylistId == null)
            throw new ArgumentException($"Playlist introuvable : '{playlistName}'");

        UserItemShareLevel? max = null;
        if (!string.IsNullOrWhiteSpace(request.MaxShareLevel))
        {
            if (!Enum.TryParse<UserItemShareLevel>(request.MaxShareLevel, ignoreCase: true, out var parsed))
                throw new ArgumentException($"MaxShareLevel invalide : '{request.MaxShareLevel}'");
            max = parsed;
        }

        var itemId = long.Parse(before.PlaylistId);
        using (WriteScope.Enter())
        {
            _itemRepository.DeleteUserItemShares(itemId, max);
        }

        return new SpikeU13ShareResultDto { Before = before, After = ReadShares(playlistName) };
    });

    // ---- Aides ---------------------------------------------------------------------------------------------

    private SpikeU13SharesDto ReadShares(string playlistName)
    {
        var playlist = FindPlaylistExact(playlistName);
        if (playlist == null) return new SpikeU13SharesDto { PlaylistName = playlistName, PlaylistFound = false };

        var rows = _itemRepository.GetUserItemShares(
            new UserItemShareQuery { ItemIds = new[] { playlist.InternalId } }, CancellationToken.None);

        var dto = new SpikeU13SharesDto { PlaylistId = playlist.InternalId.ToString(), PlaylistName = playlistName, PlaylistFound = true };
        foreach (var row in rows)
        {
            User? user = null;
            try { user = _userManager.GetUserById(_userManager.GetGuid(row.UserId)); } catch { /* id interne orphelin : ignorer le nom */ }
            dto.Rows.Add(new SpikeU13ShareRowDto
            {
                RawUserId = row.UserId,
                UserId = _userManager.GetGuid(row.UserId).ToString("N"),
                UserName = user?.Name,
                Level = (row.ShareLevel ?? UserItemShareLevel.None).ToString()
            });
        }
        return dto;
    }

    private Playlist? FindPlaylistExact(string name) =>
        _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { "Playlist" }, Recursive = true })
            .OfType<Playlist>()
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    private static string RequirePlaylistName(string? playlistName)
    {
        if (string.IsNullOrWhiteSpace(playlistName) || !playlistName.StartsWith(PlaylistPrefix, StringComparison.Ordinal))
            throw new ArgumentException($"PlaylistName doit commencer par '{PlaylistPrefix}'");
        return playlistName;
    }

    private User RequireTestUser(string? targetUserId)
    {
        if (string.IsNullOrWhiteSpace(targetUserId))
            throw new ArgumentException("TargetUserId requis");
        var user = _userManager.GetUserById(targetUserId);
        if (user == null || !user.Name.StartsWith(TestUserPrefix, StringComparison.Ordinal))
            throw new ArgumentException($"TargetUserId doit désigner un compte '{TestUserPrefix}*'");
        return user;
    }

    private static object Run(Func<object> action)
    {
        if (Plugin.Instance?.Configuration.EnableSpikeU13 != true)
            throw new ResourceNotFoundException("Spike U13 disabled");
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is not ResourceNotFoundException && ex is not ArgumentException)
        {
            PluginRuntime.Log?.Error(LogFormat.FilePrefix + "SpikeU13 : erreur non gérée", ex);
            throw new InvalidOperationException($"error: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
