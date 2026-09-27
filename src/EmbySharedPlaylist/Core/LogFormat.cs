namespace EmbySharedPlaylist.Core;

/// <summary>
/// Formatage pur des lignes de journal Emby. Fichier : préfixe « EmbySharedPlaylist : » (Emby ajoute horodatage et niveau) ;
/// console : préfixe court « [EmbySharedPlaylist] » comme « [VirtualLib] » (l'horodatage vient de <c>kubectl logs --timestamps</c>).
/// Ids seulement : jamais de nom d'utilisateur, jeton, IP, chemin ni contenu d'étiquette ou de description dans les LIGNES du plugin
/// et sur la console. Cela ne s'applique pas à la pile d'une exception écrite par l'ILogger dans embyserver.txt (chemins possibles).
/// </summary>
public static class LogFormat
{
    public const string FilePrefix = "EmbySharedPlaylist : ";
    public const string ConsolePrefix = "[EmbySharedPlaylist] ";

    private static string S(string? v) => string.IsNullOrEmpty(v) ? "-" : v;

    /// <summary>Ligne écrite une seule fois à l'initialisation du plugin (<see cref="EmbySharedPlaylist.Emby.PluginRuntime"/>).</summary>
    public static string Startup() => FilePrefix + "démarré";

    /// <summary>Ligne écrite quand la configuration du plugin est sauvegardée (le SDK n'expose pas d'événement dédié).</summary>
    public static string ConfigSaved() => FilePrefix + "configuration enregistrée";

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
        if (rest.StartsWith(FilePrefix, StringComparison.Ordinal)) rest = rest.Substring(FilePrefix.Length);
        return ConsolePrefix + (isError ? "ERROR " : string.Empty) + rest + (exceptionType != null ? " (" + exceptionType + ")" : string.Empty);
    }
}
