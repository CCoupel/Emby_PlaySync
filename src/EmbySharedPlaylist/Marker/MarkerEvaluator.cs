using System.Text.RegularExpressions;

namespace EmbySharedPlaylist.Marker;

/// <summary>
/// Évalue l'état d'une famille d'étiquettes (fonction pure, aucune écriture). Format <c>&lt;famille&gt;=NON|OUI</c> ;
/// casse ignorée, espaces tolérés autour de <c>=</c> (et en bordure) ; les étiquettes étrangères et les variantes
/// voisines (<c>remove-si-lu-oui</c>, <c>remove-si-lu=OUIX</c>, <c>remove-si-lu=</c>) sont ignorées. Les deux familles
/// n'ont aucune interdépendance.
/// </summary>
public static class MarkerEvaluator
{
    public static string FamilyName(MarkerFamily family) => family switch
    {
        MarkerFamily.RemoveSiLu => "remove-si-lu",
        MarkerFamily.PropagerLu => "propager-lu",
        _ => throw new ArgumentOutOfRangeException(nameof(family))
    };

    /// <summary>Étiquette posée par défaut : <c>remove-si-lu=NON</c> / <c>propager-lu=NON</c>.</summary>
    public static string NonTag(MarkerFamily family) => FamilyName(family) + "=NON";

    public static MarkerState Evaluate(IEnumerable<string>? tags, MarkerFamily family)
    {
        if (tags == null) return MarkerState.None;
        var pattern = new Regex("^\\s*" + Regex.Escape(FamilyName(family)) + "\\s*=\\s*(NON|OUI)\\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var hasOui = false;
        var hasNon = false;
        foreach (var tag in tags)
        {
            if (tag == null) continue;
            var m = pattern.Match(tag);
            if (!m.Success) continue;
            if (string.Equals(m.Groups[1].Value, "OUI", StringComparison.OrdinalIgnoreCase)) hasOui = true;
            else hasNon = true;
        }

        if (hasOui && hasNon) return MarkerState.Both;
        if (hasOui) return MarkerState.Oui;
        if (hasNon) return MarkerState.Non;
        return MarkerState.None;
    }

    /// <summary>Vrai si la famille est active : <c>=OUI</c> seule.</summary>
    public static bool IsActive(IEnumerable<string>? tags, MarkerFamily family) =>
        Evaluate(tags, family) == MarkerState.Oui;
}
