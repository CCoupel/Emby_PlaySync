using EmbySharedPlaylist.Core;

namespace EmbySharedPlaylist.Spike;

/// <summary>Règles pures du spike : édition ciblée des étiquettes et garde-fous sur les noms.</summary>
public static class SpikeRules
{
    public const string TestUserPrefix = "test_";
    public const string PlaylistPrefix = "SPIKE";

    /// <summary>Seuls les comptes de test peuvent être modifiés par le spike (jamais admin, cyril, user2...).</summary>
    public static bool IsTestUser(string? userName) =>
        userName != null && userName.StartsWith(TestUserPrefix, StringComparison.Ordinal);

    /// <summary>Garde les entrées dont Kind figure dans la liste (séparée par des virgules, insensible à la casse) ; liste vide = tout.</summary>
    public static JournalEntry[] FilterByKind(IEnumerable<JournalEntry> entries, string? kinds)
    {
        var wanted = (kinds ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return wanted.Count == 0
            ? entries.ToArray()
            : entries.Where(e => wanted.Contains(e.Kind)).ToArray();
    }

    /// <summary>
    /// Scénario de la sonde de réentrance (B52) choisi par le nom de la playlist : <c>SPIKE-P1</c> à <c>SPIKE-P6</c>
    /// (suivi de rien ou d'un séparateur : <c>SPIKE-P1-x</c> oui, <c>SPIKE-P10</c> et <c>SPIKE-P1x</c> non). 0 = aucun.
    /// </summary>
    public static int ProbeScenario(string? playlistName)
    {
        const string prefix = "SPIKE-P";
        if (playlistName == null || !playlistName.StartsWith(prefix, StringComparison.Ordinal)) return 0;
        if (playlistName.Length <= prefix.Length) return 0;
        var digit = playlistName[prefix.Length];
        if (digit < '1' || digit > '6') return 0;
        var next = prefix.Length + 1;
        if (playlistName.Length > next && char.IsLetterOrDigit(playlistName[next])) return 0;
        return digit - '0';
    }

    /// <summary>
    /// Vrai si l'absence de playlist de sonde mérite une ligne Info : une playlist SPIKE-Pn existe (<paramref name="probeNamed"/> &gt; 0)
    /// mais aucune entrée du média n'y est trouvée. Sinon (aucune playlist de sonde) c'est le cas normal de tout utilisateur : Debug.
    /// </summary>
    public static bool NoProbePlaylistIsNoteworthy(int probeNamed) => probeNamed > 0;

    /// <summary>Compte modifiable par le spike : préfixe test_ ET non administrateur (même s'il s'appelle test_x).</summary>
    public static bool IsEligibleForSpikeWrite(string? userName, bool isAdministrator) =>
        IsTestUser(userName) && !isAdministrator;

    /// <summary>Une playlist est « vue » par un utilisateur s'il a une ligne de partage (propriétaire ou membre) ou si elle est publique.</summary>
    public static bool IsVisibleToUser(bool hasShareRow, bool isPublic) => hasShareRow || isPublic;

    /// <summary>Seules les playlists dont le nom commence par SPIKE peuvent être modifiées par le spike.</summary>
    public static bool IsSpikePlaylist(string? name) =>
        name != null && name.StartsWith(PlaylistPrefix, StringComparison.Ordinal);

    public static string EnsureSpikeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "SPIKE À voir";
        return IsSpikePlaylist(name) ? name : PlaylistPrefix + "-" + name;
    }

    /// <summary>Garde les entrées dont SaveReason figure dans la liste (séparée par des virgules, insensible à la casse) ; liste vide = tout.</summary>
    public static JournalEntry[] FilterBySaveReason(IEnumerable<JournalEntry> entries, string? saveReasons)
    {
        var wanted = (saveReasons ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return wanted.Count == 0
            ? entries.ToArray()
            : entries.Where(e => e.SaveReason != null && wanted.Contains(e.SaveReason)).ToArray();
    }

    /// <summary>
    /// Ajoute puis retire uniquement les étiquettes nommées ; les autres sont conservées dans leur ordre.
    /// Comparaison insensible à la casse. <c>remove</c> est réservé au diagnostic : le moteur (v0.2.0, décision
    /// utilisateur) ne supprime JAMAIS d'étiquette. Un nom présent dans <paramref name="remove"/> est retiré même s'il est aussi ajouté.
    /// </summary>
    public static List<string> ApplyTagChanges(IEnumerable<string>? current, IEnumerable<string>? add, IEnumerable<string>? remove)
    {
        var result = (current ?? Enumerable.Empty<string>()).ToList();
        foreach (var tag in (add ?? Enumerable.Empty<string>()).Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            if (!result.Contains(tag, StringComparer.OrdinalIgnoreCase)) result.Add(tag);
        }
        var toRemove = new HashSet<string>(remove ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        result.RemoveAll(t => toRemove.Contains(t));
        return result;
    }
}
