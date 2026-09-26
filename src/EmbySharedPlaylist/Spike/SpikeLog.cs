using MediaBrowser.Model.Logging;

namespace EmbySharedPlaylist.Spike;

/// <summary>
/// Écrit dans deux canaux : l'ILogger d'Emby (fichier embyserver.txt) ET <c>Console.Error</c> avec le préfixe
/// <c>[EmbySharedPlaylist]</c> (visible par <c>kubectl logs</c> : après le démarrage, la console d'Emby ne reçoit
/// plus rien de l'ILogger). Debug : fichier seulement. Ne lève jamais d'exception (un log ne doit pas casser Emby).
/// Les lignes ne contiennent que des identifiants ; sur la console, une exception n'est rapportée que par son type
/// (le message peut contenir des chemins) — la pile complète reste dans le fichier.
/// </summary>
public sealed class SpikeLog
{
    private static readonly object ConsoleLock = new();
    private readonly ILogger? _logger;
    private readonly Func<TextWriter> _console;

    public SpikeLog(ILogger? logger, TextWriter? console = null)
    {
        _logger = logger;
        _console = console != null ? () => console : () => Console.Error;
    }

    public void Info(string line)
    {
        try { _logger?.Info("{0}", line); } catch { /* un log ne doit jamais casser Emby */ }
        WriteConsole(SpikeLogFormat.ToConsole(line));
    }

    /// <summary>Fichier seulement (ex. PlaybackProgress, périodique).</summary>
    public void Debug(string line)
    {
        try { _logger?.Debug("{0}", line); } catch { /* idem */ }
    }

    public void Error(string line, Exception ex)
    {
        try { _logger?.ErrorException("{0}", ex, line); } catch { /* idem */ }
        WriteConsole(SpikeLogFormat.ToConsole(line, isError: true, exceptionType: ex.GetType().Name));
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
