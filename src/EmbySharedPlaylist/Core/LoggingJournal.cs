namespace EmbySharedPlaylist.Core;

/// <summary>Journal décorateur : chaque décision est gardée en mémoire ET écrite dans les logs (fichier + console).</summary>
public sealed class LoggingJournal : IJournal
{
    private readonly IJournal _inner;
    private readonly Log _log;

    public LoggingJournal(IJournal inner, Log log)
    {
        _inner = inner;
        _log = log;
    }

    public void Add(JournalEntry entry)
    {
        _inner.Add(entry);
        _log.Info(LogFormat.Entry(entry));
    }
}
