using System.Text.RegularExpressions;

namespace EmbySharedPlaylist.Marker;

/// <summary>
/// Évalue l'état d'une famille d'étiquettes (fonction pure, aucune écriture). Format <c>&lt;famille&gt;=NON|OUI</c> ;
/// casse ignorée, espaces tolérés autour de <c>=</c> (et en bordure) ; les étiquettes étrangères et les variantes
/// voisines (<c>remove-si-lu-oui</c>, <c>remove-si-lu=OUIX</c>, <c>remove-si-lu=</c>) sont ignorées. Les trois familles
/// n'ont aucune interdépendance à l'évaluation (la dépendance remove-si-lu → propager-lu est appliquée par le moteur).
/// </summary>
public static class MarkerEvaluator
{
    public static string FamilyName(MarkerFamily family) => family switch
    {
        MarkerFamily.RemoveSiLu => "remove-si-lu",
        MarkerFamily.PropagerLu => "propager-lu",
        MarkerFamily.PropagerAvancement => "propager-avancement",
        _ => throw new ArgumentOutOfRangeException(nameof(family))
    };

    /// <summary>Étiquette posée par défaut : <c>&lt;famille&gt;=NON</c>.</summary>
    public static string NonTag(MarkerFamily family) => FamilyName(family) + "=NON";

    // Une expression par famille, construite une seule fois (le gestionnaire de lecture évalue à chaque transition).
    // \z (fin de chaîne stricte) et non $ : « remove-si-lu=OUI\n » n'est pas une étiquette valide.
    private static readonly Regex RemoveSiLuPattern = Build(MarkerFamily.RemoveSiLu);
    private static readonly Regex PropagerLuPattern = Build(MarkerFamily.PropagerLu);
    private static readonly Regex PropagerAvancementPattern = Build(MarkerFamily.PropagerAvancement);

    private static Regex Build(MarkerFamily family) =>
        new("^\\s*" + Regex.Escape(FamilyName(family)) + "\\s*=\\s*(NON|OUI)\\s*\\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Motif reconnu pour une famille (D19, v1.1.0) : réutilisé par <see cref="MarkerEditor"/> pour ne
    /// jamais dupliquer la définition (casse ignorée, espaces tolérés — mêmes règles qu'<see cref="Evaluate"/>).</summary>
    internal static Regex Pattern(MarkerFamily family) => family switch
    {
        MarkerFamily.RemoveSiLu => RemoveSiLuPattern,
        MarkerFamily.PropagerLu => PropagerLuPattern,
        MarkerFamily.PropagerAvancement => PropagerAvancementPattern,
        _ => throw new ArgumentOutOfRangeException(nameof(family))
    };

    public static MarkerState Evaluate(IEnumerable<string>? tags, MarkerFamily family)
    {
        if (tags == null) return MarkerState.None;
        var pattern = Pattern(family);

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
