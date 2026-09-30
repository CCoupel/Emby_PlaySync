using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace EmbySharedPlaylist.Emby;

// Contrat : contracts/http-endpoints.md, section « Diagnostics ». Réponses en PascalCase (casse réelle du sérialiseur d'Emby).
// Ids seulement : aucun nom d'utilisateur, de playlist ou de média, aucun secret.

[Route("/SharedPlaylist/Diagnostics/Journal", "GET")]
[Authenticated(Roles = "Admin")]
public class DiagnosticsJournal : IReturn<List<DiagnosticsJournalEntryDto>>
{
    /// <summary>Vide le journal après lecture.</summary>
    public bool Clear { get; set; }

    /// <summary>Filtre optionnel : Kind(s) séparés par des virgules. Sans filtre : les décisions du moteur uniquement.</summary>
    public string? Kind { get; set; }
}

public class DiagnosticsJournalEntryDto
{
    public string Ts { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string? ItemId { get; set; }
    public string? PlaylistId { get; set; }
    public string? Detail { get; set; }
}

[Route("/SharedPlaylist/Diagnostics/State", "GET")]
[Authenticated(Roles = "Admin")]
public class DiagnosticsState : IReturn<DiagnosticsStateDto>
{
}

public class DiagnosticsStateDto
{
    public List<string> SeenPlaylistIds { get; set; } = new();
    public Dictionary<string, Dictionary<string, int>> GraceCounters { get; set; } = new();
    public DiagnosticsLastPassDto LastPass { get; set; } = new();
    public DiagnosticsHandlerDto Handler { get; set; } = new();
    public int GracePasses { get; set; }

    /// <summary>v1.2.1 (D23) : compteurs des PlaybackProgress périodiques de l'avancement (jamais journalisés individuellement).</summary>
    public DiagnosticsPositionProgressDto PositionProgress { get; set; } = new();

    /// <summary>Tous les <c>Skipped</c> depuis le démarrage, par raison (y compris ceux qui ne sont pas inscrits dans le journal).</summary>
    public Dictionary<string, long> SkippedCounts { get; set; } = new();
}

public class DiagnosticsLastPassDto
{
    public string? Ts { get; set; }
    public long DurationMs { get; set; }
    public int PlaylistsSeen { get; set; }
    public int SharedManaged { get; set; }
}

public class DiagnosticsPositionProgressDto
{
    public long Propagated { get; set; }
    public long Throttled { get; set; }
    public long LockBusy { get; set; }
}

public class DiagnosticsHandlerDto
{
    public long Count { get; set; }
    public long LastMs { get; set; }
    public long MaxMs { get; set; }
}
