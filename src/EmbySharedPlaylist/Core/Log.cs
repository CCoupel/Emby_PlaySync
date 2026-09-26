using MediaBrowser.Model.Logging;

namespace EmbySharedPlaylist.Core;

/// <summary>Options de journalisation lues au moment de l'écriture (configuration du plugin).</summary>
public readonly record struct LogSettings(bool ToConsole, LogLevel Level)
{
    public static LogSettings FromPlugin()
    {
        var cfg = Plugin.Instance?.Configuration;
        return cfg == null ? new LogSettings(true, LogLevel.Info) : new LogSettings(cfg.LogToConsole, cfg.LogLevel);
    }
}

/// <summary>
/// Écrit dans deux canaux : l'ILogger d'Emby (fichier embyserver.txt) ET <c>Console.Error</c> avec le préfixe
/// <c>[EmbySharedPlaylist]</c> (visible par <c>kubectl logs</c> : après le démarrage, la console d'Emby ne reçoit
/// plus rien de l'ILogger). Options appliquées ICI (pas dans l'adaptateur) : <c>LogToConsole</c> (la console) et
/// <c>LogLevel</c> (Off : aucune ligne Info/Debug ; Info : Info+Error ; Debug : + lignes Debug, fichier seulement).
/// Debug : jamais sur la console. Ne lève jamais d'exception. « Ids seulement, jamais de chemin » vaut pour les LIGNES du plugin et
/// pour la CONSOLE : une exception n'y est rapportée que par son type. La pile complète, elle, va dans embyserver.txt (ILogger) et peut
/// contenir des chemins ou des noms : le fichier de log d'Emby est un fichier d'administration, pas une sortie publique.
/// </summary>
public sealed class Log
{
    private static readonly object ConsoleLock = new();
    private readonly ILogger? _logger;
    private readonly Func<TextWriter> _console;
    private readonly Func<LogSettings> _settings;

    public Log(ILogger? logger, TextWriter? console = null, Func<LogSettings>? settings = null)
    {
        _logger = logger;
        _console = console != null ? () => console : () => Console.Error;
        _settings = settings ?? LogSettings.FromPlugin;
    }

    public void Info(string line)
    {
        var s = Settings();
        if (s.Level == LogLevel.Off) return;
        try { _logger?.Info("{0}", line); } catch { /* un log ne doit jamais casser Emby */ }
        if (s.ToConsole) WriteConsole(LogFormat.ToConsole(line));
    }

    /// <summary>Fichier seulement, et seulement au niveau Debug (ex. PlaybackProgress, périodique).</summary>
    public void Debug(string line)
    {
        if (Settings().Level != LogLevel.Debug) return;
        try { _logger?.Debug("{0}", line); } catch { /* idem */ }
    }

    /// <summary>Erreur : toujours dans le fichier ; sur la console si LogToConsole et niveau ≠ Off.</summary>
    public void Error(string line, Exception ex)
    {
        var s = Settings();
        try { _logger?.ErrorException("{0}", ex, line); } catch { /* idem */ }
        if (s.ToConsole && s.Level != LogLevel.Off)
            WriteConsole(LogFormat.ToConsole(line, isError: true, exceptionType: ex.GetType().Name));
    }

    private LogSettings Settings()
    {
        try { return _settings(); } catch { return new LogSettings(true, LogLevel.Info); }
    }

    private void WriteConsole(string text)
    {
        try
        {
            lock (ConsoleLock) { _console().WriteLine(text); }
        }
        catch { /* console fermée ou saturée : on ignore */ }
    }
}
