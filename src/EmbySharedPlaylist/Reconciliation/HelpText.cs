namespace EmbySharedPlaylist.Reconciliation;

/// <summary>
/// Message d'aide écrit dans la description d'une playlist gérée. <see cref="V1"/> (v0.2.0) est posé UNIQUEMENT si la
/// description est vide (jamais par-dessus un texte). <see cref="V2"/> (v0.3.0, #51) le remplace une fois la propagation
/// effective, uniquement si la description est encore identique à <see cref="V1"/> caractère pour caractère (comparaison
/// sans état : une fois remplacée, la description ne vaut plus <see cref="V1"/>, la comparaison échoue naturellement aux
/// passes suivantes). Français seul, sans donnée personnelle.
/// </summary>
public static class HelpText
{
    public const string V1 =
        "Playlist partagée gérée par Emby Shared Playlist.\n" +
        "Deux étiquettes (Modifier les métadonnées > Mot-clé) règlent son comportement. Elles sont à NON par défaut : rien ne change.\n" +
        "- remove-si-lu=OUI : un média qui passe à « lu » est retiré de la playlist.\n" +
        "- propager-lu=OUI : l'état de lecture (lu, avancement) est propagé aux autres membres (fonction à venir).\n" +
        "Pour activer une option, remplacez NON par OUI : ajoutez l'étiquette « ...=OUI » et retirez « ...=NON » (si les deux sont présentes, NON l'emporte).";

    public const string V2 =
        "Playlist partagée gérée par Emby Shared Playlist.\n" +
        "Deux étiquettes (Modifier les métadonnées > Mot-clé) règlent son comportement. Elles sont à NON par défaut : rien ne change.\n" +
        "- remove-si-lu=OUI : un média qui passe à « lu » est retiré de la playlist.\n" +
        "- propager-lu=OUI : quand un média est lu par un membre, le flag « lu » est posé chez les autres ; l'avancement de lecture (position, pause) est aussi propagé.\n" +
        "Pour activer une option, remplacez NON par OUI : ajoutez l'étiquette « ...=OUI » et retirez « ...=NON » (si les deux sont présentes, NON l'emporte).";

    /// <summary>Alias historique de <see cref="V1"/> (v0.2.0), conservé pour compatibilité de lecture.</summary>
    public const string Message = V1;
}
