using EmbySharedPlaylist.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Dto;

namespace EmbySharedPlaylist.Emby;

/// <summary>Une entrée de playlist : son identifiant d'entrée (PlaylistItemId, instable) et l'identifiant du média.</summary>
public sealed record PlaylistEntry(long EntryId, long ItemId);

/// <summary>Résultat de lecture : les entrées et la stratégie qui a répondu (ou le détail des essais infructueux, ids seulement).</summary>
public sealed record EntryReadResult(IReadOnlyList<PlaylistEntry> Entries, string Strategy);

/// <summary>
/// Lit les entrées d'une playlist AVEC leur identifiant d'entrée. Constat (QA v0.1.0 puis essai U11) :
/// <c>playlist.GetChildren(new InternalItemsQuery())</c> — sans utilisateur — renvoie une liste vide ; la lecture doit se faire
/// dans le contexte d'un utilisateur (comme l'API REST). Stratégies, dans l'ordre, la première qui renvoie au moins une entrée
/// dotée d'un identifiant d'entrée non nul l'emporte : (1) l'utilisateur préféré (celui de l'événement) ; (2) chaque membre de
/// la playlist (propriétaire d'abord) ; (3) sans utilisateur ; (4) requête bibliothèque par <c>ListIds</c>. La stratégie
/// gagnante est rapportée pour le diagnostic. Ne lève jamais : une stratégie en échec est notée et la suivante est essayée.
/// </summary>
public sealed class PlaylistEntryReader
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IItemRepository _itemRepository;

    public PlaylistEntryReader(ILibraryManager libraryManager, IUserManager userManager, IItemRepository itemRepository)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _itemRepository = itemRepository;
    }

    public EntryReadResult Read(Playlist playlist, User? preferredUser = null)
    {
        var tried = new List<string>();

        EntryReadResult? Try(string name, Func<BaseItem[]> read)
        {
            try
            {
                var items = read() ?? Array.Empty<BaseItem>();
                var entries = items.Where(i => i.ListItemEntryId != 0).Select(i => new PlaylistEntry(i.ListItemEntryId, i.InternalId)).ToList();
                if (entries.Count > 0) return new EntryReadResult(entries, name);
                tried.Add($"{name}:{items.Length}items/{entries.Count}entries");
            }
            catch (Exception ex)
            {
                tried.Add($"{name}:{ex.GetType().Name}");
            }
            return null;
        }

        if (preferredUser != null)
        {
            var r = Try("children-user", () => playlist.GetChildren(new InternalItemsQuery(preferredUser)));
            if (r != null) return r;
        }

        foreach (var member in MemberUsers(playlist))
        {
            if (preferredUser != null && member.InternalId == preferredUser.InternalId) continue;
            var r = Try("children-member", () => playlist.GetChildren(new InternalItemsQuery(member)));
            if (r != null) return r;
        }

        var none = Try("children-nouser", () => playlist.GetChildren(new InternalItemsQuery()));
        if (none != null) return none;

        var byList = Try("listids", () => _libraryManager.GetItemList(new InternalItemsQuery { ListIds = new[] { playlist.InternalId } }));
        if (byList != null) return byList;

        return new EntryReadResult(Array.Empty<PlaylistEntry>(), "none[" + string.Join(",", tried) + "]");
    }

    /// <summary>Membres de la playlist (lignes de partage ≥ Read), le propriétaire (ManageDelete) en premier.</summary>
    private IEnumerable<User> MemberUsers(Playlist playlist)
    {
        UserItemShare[] rows;
        try
        {
            rows = _itemRepository.GetUserItemShares(new UserItemShareQuery { ItemIds = new[] { playlist.InternalId } }, CancellationToken.None);
        }
        catch
        {
            yield break;
        }

        foreach (var row in rows.Where(r => (r.ShareLevel ?? UserItemShareLevel.None) >= UserItemShareLevel.Read)
                     .OrderByDescending(r => r.ShareLevel ?? UserItemShareLevel.None))
        {
            User? user = null;
            try { user = _userManager.GetUserById(row.UserId); } catch { /* utilisateur supprimé : on passe */ }
            if (user != null) yield return user;
        }
    }
}
