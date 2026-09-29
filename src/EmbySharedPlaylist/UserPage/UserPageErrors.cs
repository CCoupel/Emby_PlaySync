namespace EmbySharedPlaylist.UserPage;

/// <summary>
/// Codes d'erreur stables des endpoints <c>User/*</c> (page utilisateur, D20, v1.1.0, #39 —
/// <c>contracts/http-endpoints.md</c>). Corps de réponse <c>{ "Error": "&lt;code&gt;" }</c>, aucun texte localisé ni
/// message d'exception côté serveur (la page traduit FR/EN depuis le code). <see cref="Internal"/> n'est jamais
/// renvoyé par une règle métier : uniquement par le garde-fou « ne lève jamais » de
/// <see cref="UserPlaylistService"/> (toute exception inattendue d'un port).
/// </summary>
public static class UserPageErrors
{
    /// <summary>403 — porte d'entrée commune, évaluée EN PREMIER pour les cinq méthodes du service.</summary>
    public const string SharingDisabled = "sharing-disabled";

    /// <summary>404 — playlist inexistante OU non possédée (indiscernables, anti-IDOR) ; ou utilisateur ciblé non membre (retrait).</summary>
    public const string NotFound = "not-found";

    /// <summary>400 — autre que <c>"Read"</c>/<c>"Write"</c> exacts (comparaison sensible à la casse, jamais Manage/ManageDelete/None).</summary>
    public const string InvalidLevel = "invalid-level";

    /// <summary>400 — le demandeur cible lui-même (ajout) ou la ligne du propriétaire (retrait).</summary>
    public const string Self = "self";

    /// <summary>400 — utilisateur cible inconnu ou désactivé.</summary>
    public const string InvalidUser = "invalid-user";

    /// <summary>409 — bascule d'option sur une playlist non partagée (le plugin ne gère rien qui ne soit pas partagé).</summary>
    public const string NotShared = "not-shared";

    /// <summary>400 — famille hors <c>{"remove-si-lu","propager-lu","propager-avancement"}</c> (comparaison exacte, sensible à la casse).</summary>
    public const string InvalidFamily = "invalid-family";

    /// <summary>409 — verrou de la playlist non obtenu dans le budget (<see cref="Core.PlaylistLocks.DefaultTimeout"/> par défaut).</summary>
    public const string Busy = "busy";

    /// <summary>400 (v1.2.0, #55) — nom vide/blanc, absent, &gt; 100 caractères après normalisation, ou caractère de contrôle.</summary>
    public const string InvalidName = "invalid-name";

    /// <summary>409 (v1.2.0, #55) — le demandeur possède déjà une playlist de ce nom (normalisé, insensible à la casse).</summary>
    public const string NameExists = "name-exists";

    /// <summary>409 (v1.2.0, #55, audit M1) — le demandeur possède déjà <see cref="UserPlaylistService.MaxOwnedPlaylists"/> playlists ou plus.</summary>
    public const string LimitReached = "limit-reached";

    /// <summary>500 — exception inattendue capturée, journalisée en <c>Error</c>, jamais de détail renvoyé à l'appelant.</summary>
    public const string Internal = "internal";
}
