namespace EmbySharedPlaylist.Core;

/// <summary>
/// Formatage pur des lignes de journal Emby. Fichier : préfixe « EmbySharedPlaylist : » (Emby ajoute horodatage et niveau) ;
/// console : préfixe court « [EmbySharedPlaylist] » comme « [VirtualLib] » (l'horodatage vient de <c>kubectl logs --timestamps</c>).
/// Ids seulement : jamais de nom d'utilisateur, jeton, IP, chemin ni contenu d'étiquette ou de description.
/// </summary>
public static class LogFormat
{
    public const string FilePrefix = "EmbySharedPlaylist : ";
    public const string SpikeFilePrefix = "EmbySharedPlaylist spike : ";
    public const string ConsolePrefix = "[EmbySharedPlaylist] ";

    private static string B(bool v) => v ? "true" : "false";
    private static string S(string? v) => string.IsNullOrEmpty(v) ? "-" : v;

    public static string Startup(bool spikeEndpointsEnabled) =>
        FilePrefix + "écouteurs du spike enregistrés (EnableSpikeEndpoints=" + B(spikeEndpointsEnabled) + ")";

    /// <summary>Ligne écrite quand la configuration du plugin est sauvegardée (valeur courante de l'option).</summary>
    public static string ConfigSaved(bool spikeEndpointsEnabled) =>
        FilePrefix + "configuration enregistrée (EnableSpikeEndpoints=" + B(spikeEndpointsEnabled) + ")";

    /// <summary>Ligne pour une décision journalisée du moteur (kinds ScanPass, MarkerPosed, DescriptionWritten, MarkerSeen, Removal, Skipped, Error).</summary>
    public static string Entry(JournalEntry e) => e.Kind switch
    {
        "ScanPass" => $"{FilePrefix}ScanPass {S(e.Detail)}",
        "Removal" => $"{FilePrefix}Removal playlist={S(e.PlaylistId)} item={S(e.ItemId)} user={S(e.UserId)} {S(e.Detail)}",
        "Error" => $"{FilePrefix}Error playlist={S(e.PlaylistId)} {S(e.Detail)}",
        _ => $"{FilePrefix}{S(e.Kind)} playlist={S(e.PlaylistId)} {S(e.Detail)}"
    };

    /// <summary>
    /// Version console d'une ligne du fichier : préfixe court, une erreur porte « ERROR » et le seul type d'exception
    /// (le message peut contenir des chemins ; la pile complète reste dans le fichier).
    /// </summary>
    public static string ToConsole(string line, bool isError = false, string? exceptionType = null)
    {
        var rest = line;
        if (rest.StartsWith(SpikeFilePrefix, StringComparison.Ordinal)) rest = rest.Substring(SpikeFilePrefix.Length);
        else if (rest.StartsWith(FilePrefix, StringComparison.Ordinal)) rest = rest.Substring(FilePrefix.Length);
        return ConsolePrefix + (isError ? "ERROR " : string.Empty) + rest + (exceptionType != null ? " (" + exceptionType + ")" : string.Empty);
    }
}
