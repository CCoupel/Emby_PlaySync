namespace EmbySharedPlaylist.Core;

/// <summary>Levée quand la relecture après une écriture du plugin ne contient pas tout ce qui devait s'y trouver. Journalisée par son seul type.</summary>
public sealed class WriteVerificationException : Exception
{
    public WriteVerificationException() : base("post-write verification failed") { }
}

/// <summary>
/// Vérification pure après <c>ApplyDefaults</c> : la relecture doit contenir TOUT l'avant (étiquettes déjà présentes) + les ajouts, et la
/// description écrite. Elle ne corrige rien et ne supprime jamais rien : elle ne fait que constater.
/// </summary>
public static class WriteVerifier
{
    /// <returns>Vrai si <paramref name="after"/> contient chaque étiquette de <paramref name="before"/> et de <paramref name="added"/>,
    /// et si la description écrite (le cas échéant) est celle relue.</returns>
    public static bool Verify(IEnumerable<string>? before, IEnumerable<string>? added, IEnumerable<string>? after,
        string? overviewWritten, string? overviewAfter)
    {
        var present = new HashSet<string>(after ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        foreach (var tag in (before ?? Enumerable.Empty<string>()).Concat(added ?? Enumerable.Empty<string>()))
            if (!present.Contains(tag)) return false;

        return overviewWritten == null || string.Equals(overviewWritten, overviewAfter, StringComparison.Ordinal);
    }
}
