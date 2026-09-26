namespace EmbySharedPlaylist.Reconciliation;

/// <summary>
/// Message d'aide écrit dans la description d'une playlist gérée, UNIQUEMENT si elle est vide (jamais par-dessus un texte).
/// Texte final D-h (docs/chronogrammes.md, « Message d'aide »), français seul, sans donnée personnelle.
/// </summary>
public static class HelpText
{
    public const string Message =
        "Playlist partagée gérée par Emby Shared Playlist.\n" +
        "Deux étiquettes (Modifier les métadonnées > Mot-clé) règlent son comportement. Elles sont à NON par défaut : rien ne change.\n" +
        "- remove-si-lu=OUI : un média qui passe à « lu » est retiré de la playlist.\n" +
        "- propager-lu=OUI : l'état de lecture (lu, avancement) est propagé aux autres membres (fonction à venir).\n" +
        "Pour activer une option, remplacez NON par OUI : ajoutez l'étiquette « ...=OUI » et retirez « ...=NON » (si les deux sont présentes, NON l'emporte).";
}
