namespace EmbySharedPlaylist.Spike;

/// <summary>Journal mémoire borné, thread-safe. Le plus récent est en dernier.</summary>
public sealed class EventJournal
{
    public const int DefaultCapacity = 500;

    private readonly object _lock = new();
    private readonly Queue<JournalEntry> _entries = new();
    private readonly int _capacity;

    public EventJournal(int capacity = DefaultCapacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public void Add(JournalEntry entry)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        lock (_lock)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > _capacity) _entries.Dequeue();
        }
    }

    /// <summary>Copie du journal ; <paramref name="clear"/> vide le journal dans la même section critique.</summary>
    public JournalEntry[] Snapshot(bool clear = false)
    {
        lock (_lock)
        {
            var copy = _entries.ToArray();
            if (clear) _entries.Clear();
            return copy;
        }
    }
}
