using EmbySharedPlaylist.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Dto;

namespace EmbySharedPlaylist.Emby;

/// <summary>
/// Adaptateur du port <see cref="IShareGateway"/> (D20, v1.1.0, #39) sur le SDK Emby : uniquement des appels internes
/// (<c>GetUserItemShares</c>/<c>SaveUserItemShares</c>/<c>DeleteUserItemShares</c>), jamais de SQL. Distinct
/// d'<see cref="EmbyPlaylistGateway"/> (tags/description/retrait) : ce gateway ne s'occupe que de la propriété et des
/// lignes de partage.
///
/// Sémantiques confirmées par le spike U13 et sa vérification QUALIF
/// (<c>_work/reports/spike-u13-20260928-144959.md</c>, <c>_work/reports/spike-u13-verification-20260928-151423.md</c>) :
/// <list type="bullet">
/// <item><b>Propriétaire</b> = ligne <c>ManageDelete</c>, posée par Emby lui-même dès la CRÉATION de la playlist,
/// avant tout partage (question c, confirmé en réel).</item>
/// <item><b>SaveUserItemShares(UserItemShare[])</b> = upsert par couple (ItemId,UserId) : n'affecte QUE les lignes
/// passées, jamais les autres lignes existantes de l'item (question e, confirmé en réel — pas de
/// lecture-modification-écriture nécessaire pour un ajout/mise à jour).</item>
/// <item><b>DeleteUserItemShares(itemId, maxShareLevel)</b> = supprime TOUTES les lignes de niveau ≤ seuil
/// (<c>Read &lt; Write &lt; Manage &lt; ManageDelete</c>) ; <c>maxShareLevel = null</c> supprime TOUT, Y COMPRIS la
/// ligne <c>ManageDelete</c> du propriétaire (confirmé en réel, risque explicitement documenté par qa). Cette
/// signature ne cible donc JAMAIS un seul utilisateur : voir <see cref="DeleteShare"/>.</item>
/// </list>
/// </summary>
public sealed class EmbyShareGateway : IShareGateway
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IItemRepository _itemRepository;
    private readonly PlaylistEntryReader _entries;

    /// <summary>Verrou d'écriture propre à ce gateway (purge+ré-écriture de <see cref="DeleteShare"/>) : distinct de
    /// celui d'<see cref="EmbyPlaylistGateway"/> (étiquettes) et de <see cref="PlaylistLocks"/> (déjà pris par
    /// <c>UserPlaylistService</c> avant tout appel ici) — protège spécifiquement contre deux appels concurrents DE CE
    /// GATEWAY sur le même item (défense en profondeur, la lecture-purge-écriture n'est jamais atomique côté Emby).</summary>
    private readonly object _writeGate = new();

    public EmbyShareGateway(ILibraryManager libraryManager, IUserManager userManager, IItemRepository itemRepository)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _itemRepository = itemRepository;
        _entries = new PlaylistEntryReader(libraryManager, userManager, itemRepository);
    }

    public IReadOnlyList<OwnedPlaylist> ListOwnedPlaylists(string ownerId)
    {
        var playlists = AllPlaylists();
        if (playlists.Length == 0) return Array.Empty<OwnedPlaylist>();

        var rows = _itemRepository
            .GetUserItemShares(new UserItemShareQuery { ItemIds = playlists.Select(p => p.InternalId).ToArray() }, CancellationToken.None)
            .ToLookup(r => r.ItemId);

        var result = new List<OwnedPlaylist>();
        foreach (var playlist in playlists)
        {
            var owned = ToOwned(playlist, rows[playlist.InternalId], ownerId);
            if (owned != null) result.Add(owned);
        }
        return result;
    }

    public OwnedPlaylist? GetOwned(string ownerId, string playlistId)
    {
        var playlist = FindPlaylist(playlistId);
        if (playlist == null) return null;
        var rows = _itemRepository.GetUserItemShares(new UserItemShareQuery { ItemIds = new[] { playlist.InternalId } }, CancellationToken.None);
        return ToOwned(playlist, rows, ownerId);
    }

    public bool UpsertShare(string playlistId, string userId, string level)
    {
        var playlist = FindPlaylist(playlistId);
        var target = _userManager.GetUserById(userId);
        // Level = valeur native Emby, DÉJÀ validée Read/Write par UserPlaylistService (voir IShareGateway) : correspondance
        // exacte, sensible à la casse (aucune tolérance, contrairement aux étiquettes).
        if (playlist == null || target == null || !Enum.TryParse(level, ignoreCase: false, out UserItemShareLevel parsedLevel))
            return false;

        using (WriteScope.Enter())
        {
            // Upsert confirmé (U13-e) : une seule ligne suffit, les autres lignes existantes de l'item (propriétaire
            // compris) survivent intactes — aucune lecture préalable nécessaire.
            _itemRepository.SaveUserItemShares(new[]
            {
                new UserItemShare { ItemId = playlist.InternalId, UserId = target.InternalId, ShareLevel = parsedLevel }
            });
        }
        return true;
    }

    public bool DeleteShare(string playlistId, string userId)
    {
        var playlist = FindPlaylist(playlistId);
        var target = _userManager.GetUserById(userId);
        if (playlist == null || target == null) return false;

        lock (_writeGate)
        {
            // Lecture fraîche AU MOMENT DE L'ÉCRITURE (comme EmbyPlaylistGateway.ApplyDefaults) : on ne retire que ce
            // qui est encore présent à cet instant.
            var rows = _itemRepository.GetUserItemShares(new UserItemShareQuery { ItemIds = new[] { playlist.InternalId } }, CancellationToken.None);
            var found = false;
            var toKeep = new List<UserItemShare>(rows.Length);
            foreach (var row in rows)
            {
                if (row.UserId == target.InternalId) { found = true; continue; }
                toKeep.Add(row);
            }
            if (!found) return false;

            using (WriteScope.Enter())
            {
                // JAMAIS de raccourci par seuil (DeleteUserItemShares(itemId, <niveau du membre>)) : le seuil touche
                // TOUTES les lignes de niveau ≤ ce seuil, pas une ligne ciblée — supprimerait à tort d'autres membres
                // de niveau égal ou inférieur. Seule séquence sûre (confirmée par la vérification QUALIF du spike
                // U13) : purge TOTALE (efface aussi la ligne ManageDelete du propriétaire, qui ne se repose jamais
                // automatiquement), puis ré-écriture explicite de tout ce qui doit être conservé — propriétaire
                // compris, reconstitué ici depuis la lecture fraîche ci-dessus, pas depuis un état mis en cache.
                //
                // Cette séquence n'est PAS transactionnelle côté SDK (security-audit-20260928-154256.md point 7) :
                // un échec entre les deux appels perdrait aussi la ligne ManageDelete du propriétaire. La DÉTECTION
                // de cet incident (relecture de vérification + journal dédié « OwnerLost ») vit délibérément dans
                // UserPlaylistService.RemoveMember, pas ici : ce gateway est un pur adaptateur (aucune logique de
                // journalisation métier dans un port/adaptateur — architecture hexagonale, cf. code-review) ; c'est
                // l'orchestrateur qui écrit déjà dans le journal partagé (ShareChanged/MarkerSet) et qui réutilise
                // de toute façon un GetOwned juste après cet appel pour construire sa réponse.
                _itemRepository.DeleteUserItemShares(playlist.InternalId, null);
                if (toKeep.Count > 0) _itemRepository.SaveUserItemShares(toKeep.ToArray());
            }
            return true;
        }
    }

    // ---- Aides -------------------------------------------------------------------------------------------

    private OwnedPlaylist? ToOwned(Playlist playlist, IEnumerable<UserItemShare> rows, string ownerId)
    {
        var rowList = rows as IReadOnlyList<UserItemShare> ?? rows.ToList();

        // Propriétaire = ligne ManageDelete (posée par Emby dès la création, question c). Aucune ligne ManageDelete
        // trouvée : propriété indéterminable, jamais renvoyée (même traitement qu'une playlist inexistante — anti-IDOR).
        var ownerRow = rowList.FirstOrDefault(r => (r.ShareLevel ?? UserItemShareLevel.None) == UserItemShareLevel.ManageDelete);
        if (ownerRow == null) return null;

        var ownerUser = _userManager.GetUserById(ownerRow.UserId);
        if (ownerUser == null || !string.Equals(ownerUser.Id.ToString("N"), ownerId, StringComparison.Ordinal)) return null;

        var members = new List<OwnedPlaylistMember>();
        foreach (var row in rowList)
        {
            if (row.UserId == ownerRow.UserId) continue; // Members exclut TOUJOURS le propriétaire (contrat)
            var level = row.ShareLevel ?? UserItemShareLevel.None;
            if (level < UserItemShareLevel.Read) continue; // même définition que ShareClassifier : membre = ligne ≥ Read
            var user = _userManager.GetUserById(row.UserId);
            if (user == null) continue; // compte supprimé entre-temps : ligne orpheline ignorée, pas d'exception
            members.Add(new OwnedPlaylistMember(user.Id.ToString("N"), level.ToString()));
        }

        var read = _entries.Read(playlist, ownerUser);
        var itemCount = read.Entries.Count + read.WithoutEntryId.Count;

        return new OwnedPlaylist(playlist.InternalId.ToString(), playlist.Name, itemCount, members, playlist.Tags ?? Array.Empty<string>());
    }

    private Playlist[] AllPlaylists() =>
        _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { "Playlist" }, Recursive = true })
            .OfType<Playlist>().ToArray();

    private Playlist? FindPlaylist(string playlistId) =>
        long.TryParse(playlistId, out var id) ? _libraryManager.GetItemById(id) as Playlist : null;
}
