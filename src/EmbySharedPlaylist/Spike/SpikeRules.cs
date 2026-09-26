namespace EmbySharedPlaylist.Spike;

/// <summary>Règles pures du spike : édition ciblée des étiquettes et garde-fous sur les noms.</summary>
public static class SpikeRules
{
    public const string TestUserPrefix = "test_";
    public const string PlaylistPrefix = "SPIKE";

    /// <summary>Seuls les comptes de test peuvent être modifiés par le spike (jamais admin, cyril, user2...).</summary>
    public static bool IsTestUser(string? userName) =>
        userName != null && userName.StartsWith(TestUserPrefix, StringComparison.Ordinal);

    /// <summary>Seules les playlists dont le nom commence par SPIKE peuvent être modifiées par le spike.</summary>
    public static bool IsSpikePlaylist(string? name) =>
        name != null && name.StartsWith(PlaylistPrefix, StringComparison.Ordinal);

    public static string EnsureSpikeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "SPIKE À voir";
        return IsSpikePlaylist(name) ? name : PlaylistPrefix + "-" + name;
    }

    /// <summary>
    /// Ajoute puis retire uniquement les étiquettes nommées ; les autres sont conservées dans leur ordre.
    /// Comparaison insensible à la casse. Un nom présent dans <paramref name="remove"/> est retiré même s'il est aussi ajouté.
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
