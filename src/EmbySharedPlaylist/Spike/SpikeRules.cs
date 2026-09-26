namespace EmbySharedPlaylist.Spike;

/// <summary>Règles pures du spike : édition ciblée des étiquettes et garde-fous sur les noms.</summary>
public static class SpikeRules
{
    public const string TestUserPrefix = "test_";
    public const string PlaylistPrefix = "SPIKE";

    /// <summary>Seuls les comptes de test peuvent être modifiés par le spike (jamais admin, cyril, user2...).</summary>
    public static bool IsTestUser(string? userName) =>
        userName != null && userName.StartsWith(TestUserPrefix, StringComparison.Ordinal);

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
