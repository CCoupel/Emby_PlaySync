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

/// <summary>
/// Instruction d'écriture de la description, ré-vérifiée AU MOMENT DE L'ÉCRITURE (comme les étiquettes) :
/// <see cref="RequiredCurrent"/> null = n'écrit <see cref="NewValue"/> QUE si la description est vide à cet instant (pose,
/// v0.2.0) ; non null = n'écrit QUE si la description est encore exactement égale à cette valeur (remplacement, #51, v0.3.0).
/// Le port ne connaît pas le contenu métier des textes : seule <c>Reconciliation/DefaultsService</c> sait qu'il s'agit de
/// <c>HelpText.V1</c>/<c>V2</c>.
/// </summary>
public sealed record OverviewChange(string? RequiredCurrent, string NewValue);

/// <summary>Résultat d'<see cref="IPlaylistGateway.ReplaceFamily"/> (D19, v1.1.0) : l'état des étiquettes APRÈS remplacement
/// (relu, source de vérité) et le nombre d'étiquettes de la famille retirées à cet appel.</summary>
public sealed record ReplaceFamilyResult(IReadOnlyList<string> Tags, int Removed);

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
    /// F3 (#59) : comme <see cref="RemoveOneEntry"/> avec le résultat détaillé (<see cref="RemoveOutcome"/>, entrées restantes de la cible).
    /// Implémentation par défaut : vrai → <c>Removed</c> (reste inconnu = −1, la boucle rappelle), faux → <c>NotFound</c>.
    /// </summary>
    RemoveResult RemoveEntry(string playlistId, string itemId) =>
        RemoveOneEntry(playlistId, itemId) ? new RemoveResult(RemoveOutcome.Removed) : new RemoveResult(RemoveOutcome.NotFound);

    /// <summary>
    /// Une seule lecture-écriture : chaque étiquette n'est ajoutée que si aucune étiquette de sa famille n'existe AU MOMENT
    /// DE L'ÉCRITURE ; la description suit <paramref name="overview"/> (ré-vérifiée au même instant). Jamais de suppression.
    /// </summary>
    ApplyResult ApplyDefaults(string playlistId, IReadOnlyList<MarkerFamily> familiesToPose, OverviewChange? overview);

    /// <summary>
    /// D19 (v1.1.0, #39) : action EXPLICITE du propriétaire (page utilisateur) — remplace ATOMIQUEMENT, en une seule
    /// écriture, toutes les étiquettes reconnues de <paramref name="family"/> (<see cref="EmbySharedPlaylist.Marker.MarkerEditor"/>,
    /// même motif que <see cref="EmbySharedPlaylist.Marker.MarkerEvaluator"/>) par la canonique <c>&lt;famille&gt;=OUI</c>/<c>=NON</c>
    /// selon <paramref name="enabled"/> ; les autres étiquettes (étrangères, autre famille) sont intactes. Même
    /// patron qu'<see cref="ApplyDefaults"/> : lecture fraîche AU MOMENT DE L'ÉCRITURE, écriture, relecture de
    /// vérification. Seule méthode du port qui SUPPRIME des étiquettes (D3 reste vraie pour tout comportement
    /// automatique du moteur/réconciliation — <see cref="ApplyDefaults"/> n'en supprime jamais).
    /// </summary>
    ReplaceFamilyResult ReplaceFamily(string playlistId, MarkerFamily family, bool enabled);

    /// <summary>
    /// v1.2.0 (#55, D22) : crée une playlist VIDE, de type Vidéo, dont <paramref name="ownerId"/> est propriétaire (Emby pose
    /// la ligne <c>ManageDelete</c> du créateur immédiatement, spike U14). <paramref name="name"/> est DÉJÀ normalisé et validé
    /// par <c>UserPlaylistService</c>. Renvoie l'identifiant (entier interne, en chaîne). Lève si la création échoue.
    /// </summary>
    string CreatePlaylist(string ownerId, string name);
}
