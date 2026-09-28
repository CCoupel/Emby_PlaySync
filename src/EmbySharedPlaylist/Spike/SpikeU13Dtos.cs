using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace EmbySharedPlaylist.Spike;

// SONDE TEMPORAIRE U13 (#39, branche spike/u13 uniquement, jamais mergée). Contrat ad hoc, non documenté dans
// contracts/ (spike jetable). [Authenticated] SANS Roles="Admin" : c'est justement ce qui est sondé (question a).
// Toutes les réponses en PascalCase (sérialiseur Emby, même convention que Diagnostics).

/// <summary>d : identité du demandeur résolue côté serveur (<see cref="IAuthorizationContext"/>), non falsifiable
/// par le corps de la requête (aucun UserId n'est jamais lu depuis le body/la query ici).</summary>
[Route("/SharedPlaylist/SpikeU13/Whoami", "GET")]
[Authenticated]
public class SpikeU13Whoami : IReturn<SpikeU13WhoamiDto>
{
}

public class SpikeU13WhoamiDto
{
    /// <summary>Guid (format "N") résolu par le serveur depuis le token de session. Null si non résolu (ne devrait pas
    /// arriver sous [Authenticated]).</summary>
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public bool IsAdmin { get; set; }
    public string? Client { get; set; }
    public string? Device { get; set; }
}

/// <summary>a/c : lit les lignes de partage BRUTES (<c>IItemRepository.GetUserItemShares</c>) d'une playlist SPIKE-U13-*,
/// sans aucune interprétation (ni <c>ShareClassifier</c>, ni notion de "géré"). Utilisé notamment pour la question c :
/// appeler sur une playlist qui vient d'être créée SANS jamais passer par « Gérer la collaboration ».</summary>
[Route("/SharedPlaylist/SpikeU13/Shares", "GET")]
[Authenticated]
public class SpikeU13Shares : IReturn<SpikeU13SharesDto>
{
    /// <summary>Nom EXACT de la playlist. Doit commencer par "SPIKE-U13-", sinon 400.</summary>
    public string PlaylistName { get; set; } = string.Empty;
}

public class SpikeU13SharesDto
{
    public string? PlaylistId { get; set; }
    public string PlaylistName { get; set; } = string.Empty;
    /// <summary>Vrai si aucune playlist de ce nom n'a été trouvée (les autres champs sont alors vides).</summary>
    public bool PlaylistFound { get; set; }
    public List<SpikeU13ShareRowDto> Rows { get; set; } = new();
}

public class SpikeU13ShareRowDto
{
    /// <summary>Id interne brut (long, tel que renvoyé par le SDK) — volontairement exposé ici (spike), jamais dans un DTO définitif.</summary>
    public long RawUserId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string? UserName { get; set; }
    /// <summary>Chaîne représentant <c>UserItemShareLevel?</c> ("None" si la valeur était null côté SDK).</summary>
    public string Level { get; set; } = string.Empty;
}

/// <summary>e/f : écrit UNE ligne de partage via <c>IItemRepository.SaveUserItemShares</c> (tableau à un seul élément)
/// et relit avant/après pour observer si les autres lignes existantes (propriétaire compris) survivent.</summary>
[Route("/SharedPlaylist/SpikeU13/Share", "POST")]
[Authenticated]
public class SpikeU13Share : IReturn<SpikeU13ShareResultDto>
{
    /// <summary>Doit commencer par "SPIKE-U13-", sinon 400.</summary>
    public string PlaylistName { get; set; } = string.Empty;
    /// <summary>Id externe (Guid "N") d'un compte dont le nom commence par "test_", sinon 400.</summary>
    public string TargetUserId { get; set; } = string.Empty;
    /// <summary>Read | Write | Manage | ManageDelete (voir <c>UserItemShareLevel</c>), sinon 400.</summary>
    public string Level { get; set; } = string.Empty;
}

/// <summary>e : appelle <c>IItemRepository.DeleteUserItemShares(itemId, maxShareLevel)</c> et relit avant/après pour
/// observer précisément quelles lignes disparaissent selon le seuil (signature : <c>UserItemShareLevel?</c>, donc PAS
/// un userId ciblé — voir rapport de spike).</summary>
[Route("/SharedPlaylist/SpikeU13/DeleteShares", "POST")]
[Authenticated]
public class SpikeU13DeleteShares : IReturn<SpikeU13ShareResultDto>
{
    /// <summary>Doit commencer par "SPIKE-U13-", sinon 400.</summary>
    public string PlaylistName { get; set; } = string.Empty;
    /// <summary>None | Read | Write | Manage | ManageDelete | (vide = null, "tout supprimer"), sinon 400 si non reconnu.</summary>
    public string? MaxShareLevel { get; set; }
}

public class SpikeU13ShareResultDto
{
    public SpikeU13SharesDto Before { get; set; } = new();
    public SpikeU13SharesDto After { get; set; } = new();
}
