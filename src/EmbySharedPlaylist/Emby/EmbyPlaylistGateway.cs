using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Marker;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace EmbySharedPlaylist.Emby;

/// <summary>
/// Adaptateur du port <see cref="IPlaylistGateway"/> sur le SDK Emby : uniquement des appels internes
/// (<c>GetUserItemShares</c>, <c>RemoveFromPlaylist</c>, <c>SetTags</c> + <c>UpdateToRepository</c>), jamais de SQL.
/// Les identifiants d'utilisateurs sortent en GUID hexadécimal sans tirets, ceux d'items en entier interne (chaîne).
/// Les écritures se font dans un <see cref="WriteScope"/> ; les lectures sont fraîches (aucun cache).
/// </summary>
public sealed class EmbyPlaylistGateway : IPlaylistGateway
{
    private const int CallTimeoutMs = 5000;
    /// <summary>v1.2.2 (#59, F2-b) : attente maximale de la fin du rafraîchissement Emby en file avant un retrait (dans le budget de 5 s).</summary>
    private const int RefreshIdleWaitMs = 1000;
    private const int RefreshPollMs = 25;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IItemRepository _itemRepository;
    private readonly IPlaylistManager _playlistManager;
    private readonly PlaylistEntryReader _entries;
    private readonly IJournal? _journal;
    private readonly IProviderManager? _providerManager;

    /// <summary>Verrou d'écriture unique de la passerelle : la passe planifiée et les gestionnaires peuvent se croiser.</summary>
    private readonly object _writeGate = new();

    public EmbyPlaylistGateway(ILibraryManager libraryManager, IUserManager userManager, IItemRepository itemRepository, IPlaylistManager playlistManager,
        IJournal? journal = null, IProviderManager? providerManager = null)
    {
        _journal = journal;
        _providerManager = providerManager;
        _entries = new PlaylistEntryReader(libraryManager, userManager, itemRepository);
        _libraryManager = libraryManager;
        _userManager = userManager;
        _itemRepository = itemRepository;
        _playlistManager = playlistManager;
    }

    public IReadOnlyList<PlaylistSnapshot> ListSharedPlaylists()
    {
        var playlists = AllPlaylists();
        if (playlists.Length == 0) return Array.Empty<PlaylistSnapshot>();

        var rows = _itemRepository
            .GetUserItemShares(new UserItemShareQuery { ItemIds = playlists.Select(p => p.InternalId).ToArray() }, CancellationToken.None)
            .ToLookup(r => r.ItemId);

        var result = new List<PlaylistSnapshot>();
        foreach (var playlist in playlists)
        {
            var snapshot = Snapshot(playlist, rows[playlist.InternalId]);
            if (snapshot.IsShared) result.Add(snapshot);
        }
        return result;
    }

    public IReadOnlyList<PlaylistSnapshot> ListSharedPlaylistsOfUserContaining(string userId, string itemId)
    {
        if (!long.TryParse(itemId, out var item)) return Array.Empty<PlaylistSnapshot>();
        User? member = null;
        try { member = _userManager.GetUserById(userId); } catch { /* id invalide : lecture sans utilisateur préféré */ }
        var result = new List<PlaylistSnapshot>();
        foreach (var snapshot in ListSharedPlaylists())
        {
            if (!snapshot.MemberIds.Contains(userId, StringComparer.Ordinal)) continue;
            if (long.TryParse(snapshot.Id, out var pid) && _libraryManager.GetItemById(pid) is Playlist playlist
                && ContainsItem(_entries.Read(playlist, member), item))
                result.Add(snapshot);
        }
        return result;
    }

    public PlaylistSnapshot? Get(string playlistId)
    {
        var playlist = FindPlaylist(playlistId);
        if (playlist == null) return null;
        var rows = _itemRepository.GetUserItemShares(new UserItemShareQuery { ItemIds = new[] { playlist.InternalId } }, CancellationToken.None);
        return Snapshot(playlist, rows);
    }

    public bool RemoveOneEntry(string playlistId, string itemId)
    {
        var playlist = FindPlaylist(playlistId);
        if (playlist == null || !long.TryParse(itemId, out var item)) return false;

        // v1.2.2 (#59, F2-b) : Emby met un rafraîchissement en file après chaque retrait ; son worker réécrit ListItems et RENUMÉROTE
        // les identifiants d'entrée (rowids réutilisés). On attend (borné) qu'il soit passé, puis on lit l'EntryId au plus près de l'écriture.
        WaitRefreshIdle(playlist);

        // Résolution par ItemId à l'instant : les identifiants d'entrée ne sont pas stables.
        var read = _entries.Read(playlist);
        var entry = read.Entries.FirstOrDefault(c => c.ItemId == item);
        if (entry == null)
        {
            // Repli (non vérifié en réel) : si Emby ne fournit AUCUN identifiant d'entrée (ListItemEntryId = 0) pour ce média,
            // retrait par ItemId via l'API interne du dépôt (retire d'un coup tous les doublons ; l'appel suivant ne trouve plus rien).
            if (!read.WithoutEntryId.Contains(item)) return false;
            using (WriteScope.Enter()) _itemRepository.RemoveListItemsByItemIds(playlist.InternalId, new[] { item });

            // Le repli n'est pas vérifié en réel : on RELIT la playlist et on n'affirme le succès que si le média a réellement disparu.
            if (ContainsItem(_entries.Read(playlist), item))
            {
                try { _journal?.Add(JournalEntries.SkippedEntry(null, playlistId, "no-effect")); } catch { /* jamais d'exception ici */ }
                return false;
            }
            return true;
        }

        var before = ItemCounts(read);
        using (WriteScope.Enter())
        {
            var task = _playlistManager.RemoveFromPlaylist(playlist, new[] { entry.EntryId });
            if (!task.Wait(CallTimeoutMs)) throw new TimeoutException("RemoveFromPlaylist > 5 s");
            task.GetAwaiter().GetResult();
        }

        CompensateWrongEntries(playlist, playlistId, item, before);
        return true;
    }

    /// <summary>Attente bornée (≤ 1 s) que la playlist ne soit plus en file de rafraîchissement Emby. Sans fournisseur ou en cas d'erreur : on n'attend pas.</summary>
    private void WaitRefreshIdle(Playlist playlist)
    {
        if (_providerManager == null) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (IsRefreshing(playlist.InternalId) && sw.ElapsedMilliseconds < RefreshIdleWaitMs)
                Thread.Sleep(RefreshPollMs);
        }
        catch { /* l'attente est un confort : jamais d'exception ici */ }
    }

    private bool IsRefreshing(long playlistId)
    {
        var pm = _providerManager!;
        if (pm.GetRefreshProgress(playlistId) != null) return true;
        var queue = pm.GetRefreshQueue();
        return queue != null && queue.Any(q => q.Item1 == playlistId);
    }

    private static bool HasLoss(Dictionary<long, int> before, Dictionary<long, int> after, long target) =>
        before.Any(b => b.Key != target && (after.TryGetValue(b.Key, out var n) ? n : 0) < b.Value);

    private static Dictionary<long, int> ItemCounts(EntryReadResult read)
    {
        var counts = new Dictionary<long, int>();
        foreach (var id in read.Entries.Select(e => e.ItemId).Concat(read.WithoutEntryId))
            counts[id] = counts.TryGetValue(id, out var n) ? n + 1 : 1;
        return counts;
    }

    /// <summary>
    /// Relecture après écriture : si un média AUTRE que la cible a perdu une entrée (multiensemble des ItemId avant/après),
    /// l'identifiant d'entrée était périmé. Compensation : ré-ajout de l'ItemId perdu et journal <c>Error wrong-entry</c>
    /// (ids seulement). Jamais d'exception : le retrait lui-même a réussi.
    /// </summary>
    private void CompensateWrongEntries(Playlist playlist, string playlistId, long target, Dictionary<long, int> before)
    {
        try
        {
            // Le worker de rafraîchissement (DeleteListItems puis réinsertion) n'est pas atomique : une lecture pendant son passage peut
            // voir une liste partielle. Une perte apparente est donc CONFIRMÉE (attente de fin de rafraîchissement + 2ᵉ lecture) avant toute compensation.
            var after = ItemCounts(_entries.Read(playlist));
            if (HasLoss(before, after, target))
            {
                WaitRefreshIdle(playlist);
                Thread.Sleep(RefreshPollMs * 2);
                after = ItemCounts(_entries.Read(playlist));
            }
            // Lecture vide alors qu'il y avait des entrées : lecture non fiable, on ne compense pas (jamais de ré-ajout à l'aveugle).
            if (after.Count == 0 && before.Count > 1) return;
            foreach (var (lost, count) in before)
            {
                if (lost == target) continue;
                after.TryGetValue(lost, out var now);
                for (var missing = count - now; missing > 0; missing--)
                {
                    try
                    {
                        User? owner = null;
                        try { var ownerId = Get(playlistId)?.OwnerId; if (ownerId != null) owner = _userManager.GetUserById(ownerId); } catch { /* sans propriétaire */ }
                        using (WriteScope.Enter())
                        {
                            var add = _playlistManager.AddToPlaylist(playlist, new[] { lost }, false, owner, CancellationToken.None);
                            if (!add.Wait(CallTimeoutMs)) throw new TimeoutException("AddToPlaylist > 5 s");
                            add.GetAwaiter().GetResult();
                        }
                    }
                    catch (Exception ex)
                    {
                        try { _journal?.Add(JournalEntries.ErrorEntry(null, playlistId, ex)); } catch { }
                    }
                    try { _journal?.Add(JournalEntries.Of(null, JournalEntries.Error, playlistId, $"wrong-entry removed={lost} target={target}")); } catch { }
                }
            }
        }
        catch { /* jamais d'exception ici */ }
    }

    public ApplyResult ApplyDefaults(string playlistId, IReadOnlyList<MarkerFamily> familiesToPose, OverviewChange? overview)
    {
        lock (_writeGate)
        {
            // Lecture fraîche PUIS écriture, dans la même section critique : on ne pose que ce qui manque à cet instant.
            var playlist = FindPlaylist(playlistId);
            if (playlist == null) return new ApplyResult();

            var tags = (playlist.Tags ?? Array.Empty<string>()).ToList();
            var posed = new List<MarkerFamily>();
            foreach (var family in familiesToPose)
            {
                if (MarkerEvaluator.Evaluate(tags, family) != MarkerState.None) continue;
                tags.Add(MarkerEvaluator.NonTag(family));
                posed.Add(family);
            }

            // RequiredCurrent null = n'écrit que si vide (pose, v0.2.0) ; sinon = n'écrit que si encore égale exactement (#51, v0.3.0).
            var overviewMatches = overview != null && (overview.RequiredCurrent == null
                ? string.IsNullOrWhiteSpace(playlist.Overview)
                : string.Equals(playlist.Overview, overview.RequiredCurrent, StringComparison.Ordinal));
            var overviewWritten = false;
            if (overviewMatches)
            {
                playlist.Overview = overview!.NewValue;
                overviewWritten = true;
            }

            if (posed.Count == 0 && !overviewWritten) return new ApplyResult();

            var before = (playlist.Tags ?? Array.Empty<string>()).ToList();
            var added = posed.Select(MarkerEvaluator.NonTag).ToList();
            using (WriteScope.Enter())
            {
                if (posed.Count > 0) playlist.SetTags(tags); // uniquement des ajouts : la liste relue + les nouvelles étiquettes
                playlist.UpdateToRepository(ItemUpdateType.MetadataEdit);
            }

            // Relecture : tout l'avant + les ajouts (+ la description) doivent y être ; sinon Error (type seul), jamais de suppression.
            var check = FindPlaylist(playlistId);
            if (check != null && !WriteVerifier.Verify(before, added, check.Tags, overviewWritten ? overview!.NewValue : null, check.Overview))
                throw new WriteVerificationException();
            return new ApplyResult(posed, overviewWritten);
        }
    }

    /// <summary>
    /// D19 (v1.1.0, #39) : implémenté par avance sur la Phase 2 du plan (tâche 6) — pur remplacement d'étiquettes,
    /// aucune dépendance aux constats du spike U13 (partages/propriété), contrairement aux futurs
    /// <c>EmbyShareGateway</c>/<c>EmbyUserDirectory</c>. Même verrou (<c>_writeGate</c>) et même patron que
    /// <see cref="ApplyDefaults"/> : lecture fraîche, écriture, relecture. Contrairement à <see cref="ApplyDefaults"/>
    /// (ajout seul, <see cref="WriteVerifier"/> exige avant+ajouts ⊆ après), <see cref="MarkerEditor.Replace"/>
    /// SUPPRIME des étiquettes par conception (D19) : <see cref="WriteVerifier"/> ne s'applique pas (il rejetterait
    /// toute suppression légitime). La relecture sert de source de vérité pour la valeur RETOURNÉE (jamais la valeur
    /// calculée), pas de garde bloquante : un échec de relecture (cas théorique, item disparu entre-temps) retombe
    /// sur la valeur calculée plutôt que de perdre le résultat de l'écriture déjà effectuée.
    /// </summary>
    public ReplaceFamilyResult ReplaceFamily(string playlistId, MarkerFamily family, bool enabled)
    {
        lock (_writeGate)
        {
            var playlist = FindPlaylist(playlistId);
            if (playlist == null) return new ReplaceFamilyResult(Array.Empty<string>(), 0);

            var current = (playlist.Tags ?? Array.Empty<string>()).ToList();
            var (newTags, removed) = MarkerEditor.Replace(current, family, enabled);

            using (WriteScope.Enter())
            {
                playlist.SetTags(newTags);
                playlist.UpdateToRepository(ItemUpdateType.MetadataEdit);
            }

            var check = FindPlaylist(playlistId);
            var finalTags = check?.Tags ?? newTags.ToArray();
            return new ReplaceFamilyResult(finalTags, removed);
        }
    }

    /// <summary>
    /// v1.2.0 (#55, D22, C1) : patron compilé du spike (<c>IPlaylistManager.CreatePlaylist</c>, liste vide, <c>MediaType=Video</c>) ;
    /// U14 : liste vide acceptée, <c>ManageDelete</c> du créateur posé immédiatement. Ne pas s'appuyer sur <c>MediaType</c>
    /// relu ni sur <c>CanDelete</c> (peu fiables en lecture).
    /// </summary>
    public string CreatePlaylist(string ownerId, string name)
    {
        var owner = _userManager.GetUserById(ownerId) ?? throw new InvalidOperationException("owner-not-found");
        using (WriteScope.Enter())
        {
            var task = _playlistManager.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = name,
                ItemIdList = Array.Empty<long>(),
                MediaType = "Video",
                User = owner
            });
            if (!task.Wait(CallTimeoutMs)) throw new TimeoutException("CreatePlaylist > 5 s");
            var created = task.GetAwaiter().GetResult();
            var playlist = FindPlaylist(created.Id) ?? throw new InvalidOperationException("created-playlist-not-found");
            return playlist.InternalId.ToString();
        }
    }

    // ---- Aides -------------------------------------------------------------------------------------------

    private static bool ContainsItem(EntryReadResult read, long item) =>
        read.Entries.Any(c => c.ItemId == item) || read.WithoutEntryId.Contains(item);

    private Playlist[] AllPlaylists() =>
        _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { "Playlist" }, Recursive = true })
            .OfType<Playlist>().ToArray();

    private Playlist? FindPlaylist(string playlistId) =>
        long.TryParse(playlistId, out var id) ? _libraryManager.GetItemById(id) as Playlist : null;

    private PlaylistSnapshot Snapshot(Playlist playlist, IEnumerable<UserItemShare> rows)
    {
        var classified = ShareClassifier.Classify(rows.Select(r =>
            (_userManager.GetGuid(r.UserId).ToString("N"), r.ShareLevel ?? UserItemShareLevel.None)));
        return new PlaylistSnapshot(playlist.InternalId.ToString(), classified.OwnerId, classified.MemberIds,
            playlist.Tags ?? Array.Empty<string>(), playlist.Overview);
    }
}
