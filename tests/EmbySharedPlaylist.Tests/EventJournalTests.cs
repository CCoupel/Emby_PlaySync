using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Spike;
using Xunit;

namespace EmbySharedPlaylist.Tests;

public class EventJournalTests
{
    private static JournalEntry E(string kind) => new() { Kind = kind };

    [Fact]
    public void Snapshot_ReturnsEntriesOldestFirst()
    {
        var j = new EventJournal();
        j.Add(E("a")); j.Add(E("b"));
        Assert.Equal(new[] { "a", "b" }, j.Snapshot().Select(e => e.Kind));
    }

    [Fact]
    public void Add_DropsOldestBeyondCapacity()
    {
        var j = new EventJournal(3);
        for (var i = 0; i < 5; i++) j.Add(E(i.ToString()));
        Assert.Equal(new[] { "2", "3", "4" }, j.Snapshot().Select(e => e.Kind));
    }

    [Fact]
    public void Snapshot_WithClear_EmptiesJournalAfterRead()
    {
        var j = new EventJournal();
        j.Add(E("a"));
        Assert.Single(j.Snapshot(clear: true));
        Assert.Empty(j.Snapshot());
    }

    [Fact]
    public void Snapshot_WithoutClear_KeepsEntries()
    {
        var j = new EventJournal();
        j.Add(E("a"));
        j.Snapshot();
        Assert.Single(j.Snapshot());
    }

    [Fact]
    public void Constructor_RejectsInvalidCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EventJournal(0));
    }

    [Fact]
    public void Add_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new EventJournal().Add(null!));
    }

    [Fact]
    public void Add_IsThreadSafeAndBounded()
    {
        var j = new EventJournal(100);
        Parallel.For(0, 1000, i => j.Add(E(i.ToString())));
        Assert.Equal(100, j.Snapshot().Length);
    }
}
