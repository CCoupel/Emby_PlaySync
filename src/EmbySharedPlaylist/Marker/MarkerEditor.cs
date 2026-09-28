namespace EmbySharedPlaylist.Marker;

/// <summary>
/// Éditeur PUR d'étiquettes (D19, v1.1.0, #39) : contrairement au moteur/réconciliation (qui ne posent jamais que
/// l'étiquette manquante, jamais de suppression), une action EXPLICITE du propriétaire sur la page utilisateur
/// remplace ATOMIQUEMENT toutes les étiquettes reconnues d'une famille par une seule étiquette canonique. Aucune
/// écriture ici : le gateway (<c>IPlaylistGateway.ReplaceFamily</c>) fait la lecture fraîche + l'écriture + la
/// relecture de vérification, même patron qu'<c>ApplyDefaults</c>.
/// </summary>
public static class MarkerEditor
{
    /// <summary>
    /// Retire toutes les étiquettes reconnues de <paramref name="family"/> (même motif que
    /// <see cref="MarkerEvaluator"/> : casse ignorée, espaces tolérés — jamais dupliqué, réutilisé via
    /// <see cref="MarkerEvaluator.Pattern"/>), préserve l'ordre des autres étiquettes, puis ajoute la canonique
    /// <c>&lt;famille&gt;=OUI</c>/<c>=NON</c> en fin de liste. Idempotent : ré-appliquer le même état retire la
    /// canonique déjà posée puis la repose à l'identique (<c>Removed</c> reflète toujours ce qui a été retiré à CET
    /// appel, jamais de court-circuit).
    /// </summary>
    public static (IReadOnlyList<string> NewTags, int Removed) Replace(IReadOnlyList<string> tags, MarkerFamily family, bool enabled)
    {
        var pattern = MarkerEvaluator.Pattern(family);
        var kept = new List<string>(tags.Count + 1);
        var removed = 0;
        foreach (var tag in tags)
        {
            if (tag != null && pattern.IsMatch(tag)) { removed++; continue; }
            kept.Add(tag);
        }
        kept.Add(MarkerEvaluator.FamilyName(family) + "=" + (enabled ? "OUI" : "NON"));
        return (kept, removed);
    }
}
