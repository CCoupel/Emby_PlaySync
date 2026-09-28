using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Marker;
using EmbySharedPlaylist.Reconciliation;

namespace EmbySharedPlaylist.UserPage;

/// <summary>Issue d'une méthode de <see cref="UserPlaylistService"/> : succès (<see cref="Value"/> renseigné) ou
/// échec (<see cref="Error"/> = un code de <see cref="UserPageErrors"/>).</summary>
public sealed record UserPageResult<T>(bool Ok, string? Error, T? Value)
{
    public static UserPageResult<T> Success(T value) => new(true, null, value);
    public static UserPageResult<T> Fail(string error) => new(false, error, default);
}

/// <summary>Membre d'une playlist, tel qu'exposé à la page (nom résolu via <see cref="IUserDirectory.Find"/>).</summary>
public sealed record UserPageMemberDto(string UserId, string Name, string Level);

/// <summary>Playlist possédée par le demandeur, telle qu'exposée à la page (<c>GET User/Playlists</c> et retour des
/// écritures). <see cref="Options"/> : clés = <see cref="MarkerEvaluator.FamilyName"/>, valeurs =
/// <c>MarkerState.ToString()</c> ("None"/"Non"/"Oui"/"Both") — état ÉVALUÉ (lecture fraîche), jamais une intention.</summary>
public sealed record UserPagePlaylistDto(
    string PlaylistId,
    string Name,
    int ItemCount,
    bool IsShared,
    IReadOnlyList<UserPageMemberDto> Members,
    IReadOnlyDictionary<string, string> Options);

/// <summary>Compte sélectionnable (ajout d'un membre).</summary>
public sealed record UserPageSelectableDto(string UserId, string Name);

/// <summary>
/// Service applicatif de la page utilisateur (D20, v1.1.0, #39 — <c>contracts/http-endpoints.md</c> section « Page
/// utilisateur », spec D19/D20/S10). Indépendant d'Emby (ports uniquement) : toutes les règles d'autorisation et
/// d'orchestration sont ici, testables sans SDK.
///
/// Ordre des vérifications, IDENTIQUE pour les cinq méthodes publiques :
/// <list type="number">
/// <item><see cref="IUserDirectory.CanShare"/> du DEMANDEUR — 403 <see cref="UserPageErrors.SharingDisabled"/> EN
/// PREMIER, avant tout autre calcul (même une playlist inexistante ne fuite jamais au travers).</item>
/// <item>Propriété (<see cref="IShareGateway.GetOwned"/>) — 404 <see cref="UserPageErrors.NotFound"/>, indiscernable
/// d'une playlist inexistante (anti-IDOR, CA6).</item>
/// <item>Validations de forme propres à la méthode (niveau strict <c>"Read"</c>/<c>"Write"</c>, famille stricte
/// <c>"remove-si-lu"</c>/<c>"propager-lu"</c> — comparaison EXACTE, sensible à la casse, aucune tolérance
/// contrairement aux étiquettes elles-mêmes).</item>
/// <item>Cible = soi (400 <see cref="UserPageErrors.Self"/>) ; cible inconnue/désactivée (400
/// <see cref="UserPageErrors.InvalidUser"/>).</item>
/// <item><see cref="PlaylistLocks"/> pris ENSUITE (409 <see cref="UserPageErrors.Busy"/> si non obtenu dans
/// <c>lockTimeout</c>, défaut 5 s) ; relecture SOUS le verrou avant toute écriture (anti-race).</item>
/// <item>Premier partage (playlist non partagée AVANT l'upsert) → <see cref="DefaultsService.OnFirstDetection"/>
/// appelée SOUS LE MÊME VERROU (réentrance, <see cref="PlaylistLocks"/> est réentrant sur le même fil).</item>
/// <item>Options réservées aux playlists partagées (409 <see cref="UserPageErrors.NotShared"/>).</item>
/// <item>Journal <c>ShareChanged</c>/<c>MarkerSet</c> (ids seulement).</item>
/// </list>
/// Ne lève JAMAIS : toute exception inattendue d'un port est capturée, journalisée en <c>Error</c>, retournée comme
/// <see cref="UserPageErrors.Internal"/>. Le retour (succès) est TOUJOURS la playlist relue après écriture (source de
/// vérité serveur, jamais l'état avant écriture).
/// </summary>
public sealed class UserPlaylistService
{
    private static readonly IReadOnlyDictionary<string, MarkerFamily> Families = new Dictionary<string, MarkerFamily>(StringComparer.Ordinal)
    {
        [MarkerEvaluator.FamilyName(MarkerFamily.RemoveSiLu)] = MarkerFamily.RemoveSiLu,
        [MarkerEvaluator.FamilyName(MarkerFamily.PropagerLu)] = MarkerFamily.PropagerLu
    };

    private readonly IShareGateway _shares;
    private readonly IUserDirectory _users;
    private readonly IPlaylistGateway _playlists;
    private readonly PlaylistLocks _locks;
    private readonly DefaultsService _defaults;
    private readonly IJournal _journal;
    private readonly IClock? _clock;
    private readonly TimeSpan _lockTimeout;

    public UserPlaylistService(IShareGateway shares, IUserDirectory users, IPlaylistGateway playlists, PlaylistLocks locks,
        DefaultsService defaults, IJournal journal, IClock? clock = null, TimeSpan? lockTimeout = null)
    {
        _shares = shares;
        _users = users;
        _playlists = playlists;
        _locks = locks;
        _defaults = defaults;
        _journal = journal;
        _clock = clock;
        _lockTimeout = lockTimeout ?? PlaylistLocks.DefaultTimeout;
    }

    // ---- GET User/Playlists / GET User/Users --------------------------------------------------------------

    public UserPageResult<IReadOnlyList<UserPagePlaylistDto>> ListOwned(string requesterId)
    {
        var gate = CheckSharing<IReadOnlyList<UserPagePlaylistDto>>(requesterId);
        if (gate != null) return gate;

        return Guard(null, () =>
        {
            var list = _shares.ListOwnedPlaylists(requesterId)
                .Select(ToDto)
                .OrderBy(d => d.Name, StringComparer.InvariantCulture)
                .ToList();
            return UserPageResult<IReadOnlyList<UserPagePlaylistDto>>.Success(list);
        });
    }

    public UserPageResult<IReadOnlyList<UserPageSelectableDto>> ListSelectableUsers(string requesterId)
    {
        var gate = CheckSharing<IReadOnlyList<UserPageSelectableDto>>(requesterId);
        if (gate != null) return gate;

        return Guard(null, () =>
        {
            var list = _users.ListSelectable(requesterId)
                .Select(u => new UserPageSelectableDto(u.UserId, u.Name))
                .OrderBy(u => u.Name, StringComparer.InvariantCulture)
                .ToList();
            return UserPageResult<IReadOnlyList<UserPageSelectableDto>>.Success(list);
        });
    }

    // ---- POST .../Members (upsert) -------------------------------------------------------------------------

    public UserPageResult<UserPagePlaylistDto> AddOrUpdateMember(string requesterId, string playlistId, string targetUserId, string level)
    {
        var gate = CheckSharing<UserPagePlaylistDto>(requesterId);
        if (gate != null) return gate;

        return Guard(playlistId, () =>
        {
            var owned = _shares.GetOwned(requesterId, playlistId);
            if (owned == null) return Fail(UserPageErrors.NotFound);
            if (!IsValidLevel(level)) return Fail(UserPageErrors.InvalidLevel);
            if (string.Equals(targetUserId, requesterId, StringComparison.Ordinal)) return Fail(UserPageErrors.Self);
            var target = _users.Find(targetUserId);
            if (target == null || !target.Active) return Fail(UserPageErrors.InvalidUser);

            using var lockGate = _locks.TryAcquire(playlistId, _lockTimeout);
            if (lockGate == null) return Fail(UserPageErrors.Busy);

            var fresh = _shares.GetOwned(requesterId, playlistId);
            if (fresh == null) return Fail(UserPageErrors.NotFound);

            var wasShared = fresh.IsShared;
            var wasMember = fresh.Members.Any(m => string.Equals(m.UserId, targetUserId, StringComparison.Ordinal));

            // Revue de code (code-review-20260928-154519.md, MINEUR) : le retour bool n'était jusqu'ici jamais
            // vérifié — un échec silencieux de l'adaptateur (fenêtre TOCTOU étroite entre la relecture sous verrou
            // ci-dessus et cet appel) aurait été journalisé comme un succès. Désormais vérifié explicitement.
            bool wrote;
            using (WriteScope.Enter()) wrote = _shares.UpsertShare(playlistId, targetUserId, level);
            if (!wrote)
            {
                _journal.Add(JournalEntries.Of(_clock, JournalEntries.Error, playlistId, "upsert-share-failed"));
                return Fail(UserPageErrors.Internal);
            }

            _journal.Add(JournalEntries.ShareChangedEntry(_clock, playlistId, targetUserId,
                $"action={(wasMember ? "update" : "add")} level={level}"));

            if (!wasShared)
            {
                var snapshot = _playlists.Get(playlistId);
                if (snapshot != null) _defaults.OnFirstDetection(snapshot);
            }

            return Success(playlistId, requesterId);
        });
    }

    // ---- DELETE .../Members/{UserId} -----------------------------------------------------------------------

    public UserPageResult<UserPagePlaylistDto> RemoveMember(string requesterId, string playlistId, string targetUserId)
    {
        var gate = CheckSharing<UserPagePlaylistDto>(requesterId);
        if (gate != null) return gate;

        return Guard(playlistId, () =>
        {
            var owned = _shares.GetOwned(requesterId, playlistId);
            if (owned == null) return Fail(UserPageErrors.NotFound);
            if (string.Equals(targetUserId, requesterId, StringComparison.Ordinal)) return Fail(UserPageErrors.Self);

            using var lockGate = _locks.TryAcquire(playlistId, _lockTimeout);
            if (lockGate == null) return Fail(UserPageErrors.Busy);

            var fresh = _shares.GetOwned(requesterId, playlistId);
            if (fresh == null) return Fail(UserPageErrors.NotFound);
            if (!fresh.Members.Any(m => string.Equals(m.UserId, targetUserId, StringComparison.Ordinal)))
                return Fail(UserPageErrors.NotFound);

            // Revue de code (code-review-20260928-154519.md, MINEUR) : voir même remarque qu'AddOrUpdateMember.
            bool wrote;
            using (WriteScope.Enter()) wrote = _shares.DeleteShare(playlistId, targetUserId);
            if (!wrote)
            {
                _journal.Add(JournalEntries.Of(_clock, JournalEntries.Error, playlistId, "delete-share-failed"));
                return Fail(UserPageErrors.Internal);
            }

            _journal.Add(JournalEntries.ShareChangedEntry(_clock, playlistId, targetUserId, "action=remove level=-"));

            // Détection « propriétaire perdu » (security-audit-20260928-154256.md point 7) : le port a bien retiré
            // le membre ciblé (wrote=true ci-dessus), mais la séquence purge-totale + reconstruction de
            // EmbyShareGateway.DeleteShare n'est pas transactionnelle côté SDK — un échec partiel entre les deux
            // appels perd aussi la ligne ManageDelete du propriétaire. Détecté ICI (UserPlaylistService), pas dans
            // l'adaptateur : c'est le journal partagé de l'orchestrateur (celui qui reçoit ShareChanged/MarkerSet,
            // observable par les tests ET par Diagnostics/Journal en pratique) qui doit recevoir l'alarme — jamais
            // un journal séparé propre à un adaptateur (architecture hexagonale : les ports/adaptateurs ne portent
            // aucune logique de journalisation métier). La relecture réutilisée ici est celle que Success()
            // effectuerait de toute façon pour construire la réponse : aucun appel supplémentaire au port.
            var afterDelete = _shares.GetOwned(requesterId, playlistId);
            if (afterDelete == null)
            {
                _journal.Add(JournalEntries.OwnerLostEntry(_clock, playlistId, requesterId));
                return Fail(UserPageErrors.NotFound);
            }
            return UserPageResult<UserPagePlaylistDto>.Success(ToDto(afterDelete));
        });
    }

    // ---- POST .../Options (D19) -----------------------------------------------------------------------------

    public UserPageResult<UserPagePlaylistDto> SetOption(string requesterId, string playlistId, string family, bool enabled)
    {
        var gate = CheckSharing<UserPagePlaylistDto>(requesterId);
        if (gate != null) return gate;

        return Guard(playlistId, () =>
        {
            var owned = _shares.GetOwned(requesterId, playlistId);
            if (owned == null) return Fail(UserPageErrors.NotFound);
            if (string.IsNullOrEmpty(family) || !Families.TryGetValue(family, out var parsedFamily)) return Fail(UserPageErrors.InvalidFamily);

            using var lockGate = _locks.TryAcquire(playlistId, _lockTimeout);
            if (lockGate == null) return Fail(UserPageErrors.Busy);

            var fresh = _shares.GetOwned(requesterId, playlistId);
            if (fresh == null) return Fail(UserPageErrors.NotFound);
            if (!fresh.IsShared) return Fail(UserPageErrors.NotShared);

            ReplaceFamilyResult replaced;
            using (WriteScope.Enter()) replaced = _playlists.ReplaceFamily(playlistId, parsedFamily, enabled);

            _journal.Add(JournalEntries.MarkerSetEntry(_clock, playlistId, requesterId,
                $"family={MarkerEvaluator.FamilyName(parsedFamily)} value={(enabled ? "OUI" : "NON")} removed={replaced.Removed}"));

            return Success(playlistId, requesterId);
        });
    }

    // ---- Aides -----------------------------------------------------------------------------------------------

    /// <summary>Porte d'entrée commune (403), évaluée EN PREMIER, pour les cinq méthodes.</summary>
    private UserPageResult<T>? CheckSharing<T>(string requesterId) =>
        _users.CanShare(requesterId) ? null : UserPageResult<T>.Fail(UserPageErrors.SharingDisabled);

    /// <summary>Ne lève JAMAIS : capture toute exception inattendue d'un port, journalise <c>Error</c>, renvoie <c>Internal</c>.</summary>
    private UserPageResult<T> Guard<T>(string? playlistId, Func<UserPageResult<T>> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            _journal.Add(JournalEntries.ErrorEntry(_clock, playlistId, ex));
            return UserPageResult<T>.Fail(UserPageErrors.Internal);
        }
    }

    private static UserPageResult<UserPagePlaylistDto> Fail(string error) => UserPageResult<UserPagePlaylistDto>.Fail(error);

    /// <summary>Playlist RELUE après écriture (source de vérité serveur) — jamais l'état capturé avant l'écriture.</summary>
    private UserPageResult<UserPagePlaylistDto> Success(string playlistId, string requesterId)
    {
        var fresh = _shares.GetOwned(requesterId, playlistId);
        return fresh == null ? Fail(UserPageErrors.NotFound) : UserPageResult<UserPagePlaylistDto>.Success(ToDto(fresh));
    }

    private UserPagePlaylistDto ToDto(OwnedPlaylist p)
    {
        var members = p.Members
            .Select(m => new UserPageMemberDto(m.UserId, _users.Find(m.UserId)?.Name ?? m.UserId, m.Level))
            .ToList();
        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MarkerEvaluator.FamilyName(MarkerFamily.RemoveSiLu)] = MarkerEvaluator.Evaluate(p.Tags, MarkerFamily.RemoveSiLu).ToString(),
            [MarkerEvaluator.FamilyName(MarkerFamily.PropagerLu)] = MarkerEvaluator.Evaluate(p.Tags, MarkerFamily.PropagerLu).ToString()
        };
        return new UserPagePlaylistDto(p.Id, p.Name, p.ItemCount, p.IsShared, members, options);
    }

    private static bool IsValidLevel(string? level) => level == "Read" || level == "Write";
}
