namespace EmbySharedPlaylist.Core;

/// <summary>Port du journal des décisions du plugin (implémenté par <see cref="EventJournal"/>).</summary>
public interface IJournal
{
    void Add(JournalEntry entry);
}
