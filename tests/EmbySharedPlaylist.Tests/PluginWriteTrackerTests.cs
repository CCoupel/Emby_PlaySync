using EmbySharedPlaylist.Spike;
using Xunit;

namespace EmbySharedPlaylist.Tests;

public class PluginWriteTrackerTests
{
    [Fact]
    public void TryConsume_ReturnsTrueOnceForRegisteredPair()
    {
        var t = new PluginWriteTracker();
        t.Register(1, 10);
        Assert.True(t.TryConsume(1, 10));
        Assert.False(t.TryConsume(1, 10));
    }

    [Fact]
    public void TryConsume_ReturnsFalseForUnknownOrDifferentPair()
    {
        var t = new PluginWriteTracker();
        t.Register(1, 10);
        Assert.False(t.TryConsume(2, 10));
        Assert.False(t.TryConsume(1, 11));
        Assert.Equal(1, t.Count);
    }

    [Fact]
    public void Entries_ExpireAfterTtl()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t = new PluginWriteTracker(TimeSpan.FromMinutes(1), now: () => now);
        t.Register(1, 10);
        now = now.AddSeconds(61);
        Assert.False(t.TryConsume(1, 10));
        Assert.Equal(0, t.Count);
    }

    [Fact]
    public void Entries_DoNotExpireBeforeTtl()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t = new PluginWriteTracker(TimeSpan.FromMinutes(1), now: () => now);
        t.Register(1, 10);
        now = now.AddSeconds(59);
        Assert.True(t.TryConsume(1, 10));
    }

    [Fact]
    public void Register_EvictsOldestWhenCapacityReached()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t = new PluginWriteTracker(TimeSpan.FromHours(1), capacity: 2, now: () => now);
        t.Register(1, 1); now = now.AddSeconds(1);
        t.Register(2, 2); now = now.AddSeconds(1);
        t.Register(3, 3);
        Assert.Equal(2, t.Count);
        Assert.False(t.TryConsume(1, 1));
        Assert.True(t.TryConsume(2, 2));
        Assert.True(t.TryConsume(3, 3));
    }

    [Fact]
    public void Register_SamePairTwice_KeepsSingleEntry()
    {
        var t = new PluginWriteTracker();
        t.Register(1, 1); t.Register(1, 1);
        Assert.Equal(1, t.Count);
    }

    [Fact]
    public void Constructor_RejectsInvalidCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PluginWriteTracker(capacity: 0));
    }

    [Fact]
    public void Tracker_IsThreadSafe()
    {
        var t = new PluginWriteTracker(capacity: 50);
        Parallel.For(0, 1000, i => { t.Register(i % 10, i); t.TryConsume(i % 10, i - 1); });
        Assert.True(t.Count <= 50);
    }
}
