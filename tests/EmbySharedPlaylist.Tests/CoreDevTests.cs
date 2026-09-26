using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Marker;
using Moq;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>Tests internes de dev-plugin : mémoire des playlists vues, snapshot, journal, configuration.</summary>
public class SeenPlaylistsDevTests
{
    [Fact]
    public void TryMarkSeen_IsTrueOnlyOnce()
    {
        var s = new SeenPlaylists();
        Assert.True(s.TryMarkSeen("1"));
        Assert.False(s.TryMarkSeen("1"));
        Assert.True(s.IsSeen("1"));
        Assert.False(s.IsSeen("2"));
    }

    [Fact]
    public void TryMarkSeen_IsAtomicUnderConcurrency()
    {
        var s = new SeenPlaylists();
        var winners = 0;
        Parallel.For(0, 64, _ => { if (s.TryMarkSeen("p")) Interlocked.Increment(ref winners); });
        Assert.Equal(1, winners);
    }

    [Fact]
    public void Capacity_ForgetsTheOldestPlaylistAndItsCounters()
    {
        var s = new SeenPlaylists(3);
        for (var i = 1; i <= 3; i++) s.TryMarkSeen(i.ToString());
        s.Bump("1", "remove-si-lu");
        s.TryMarkSeen("4");
        Assert.False(s.IsSeen("1"));
        Assert.Equal(0, s.Get("1", "remove-si-lu"));
        Assert.True(s.IsSeen("2") && s.IsSeen("3") && s.IsSeen("4"));
        Assert.Equal(3, s.Count);
    }

    [Fact]
    public void Bump_CountsPerPlaylistAndKey_ResetClearsOnlyThatKey()
    {
        var s = new SeenPlaylists();
        s.TryMarkSeen("p");
        Assert.Equal(1, s.Bump("p", "remove-si-lu"));
        Assert.Equal(2, s.Bump("p", "remove-si-lu"));
        Assert.Equal(1, s.Bump("p", "propager-lu"));
        s.Reset("p", "remove-si-lu");
        Assert.Equal(0, s.Get("p", "remove-si-lu"));
        Assert.Equal(1, s.Get("p", "propager-lu"));
        Assert.Equal(1, s.Bump("p", "remove-si-lu"));
    }

    [Fact]
    public void Bump_OnAnUnseenPlaylist_DoesNothing()
    {
        var s = new SeenPlaylists();
        Assert.Equal(0, s.Bump("unseen", "description"));
        Assert.Empty(s.Counters());
    }

    [Fact]
    public void Snapshots_AreCopies()
    {
        var s = new SeenPlaylists();
        s.TryMarkSeen("p");
        s.Bump("p", "description");
        var ids = s.SeenIds();
        var counters = s.Counters();
        s.TryMarkSeen("q");
        Assert.Equal(new[] { "p" }, ids);
        Assert.Equal(1, counters["p"]["description"]);
    }

    [Fact]
    public void Constructor_RejectsInvalidCapacity() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new SeenPlaylists(0));

    [Fact]
    public void TryMarkSeen_RejectsEmptyId() =>
        Assert.Throws<ArgumentException>(() => new SeenPlaylists().TryMarkSeen(""));
}

public class PlaylistSnapshotDevTests
{
    private static PlaylistSnapshot P(string? owner, params string[] members) =>
        new("1", owner, members, Array.Empty<string>(), null);

    [Fact]
    public void IsShared_RequiresKnownOwnerAndAnotherMember()
    {
        Assert.True(P("o", "o", "m").IsShared);
        Assert.False(P("o", "o").IsShared);
        Assert.False(P(null, "a", "b").IsShared);
        Assert.False(P("o").IsShared);
    }
}

public class JournalDevTests
{
    [Fact]
    public void EventJournal_ImplementsIJournal_AndFiltersByKind()
    {
        IJournal j = new EventJournal();
        j.Add(new JournalEntry { Kind = "ScanPass" });
        j.Add(new JournalEntry { Kind = "Skipped" });
        var ej = (EventJournal)j;
        Assert.Equal(new[] { "Skipped" }, ej.Snapshot(false, "skipped").Select(e => e.Kind));
        Assert.Equal(2, ej.Snapshot(false, null).Length);
        Assert.Equal(2, ej.Snapshot(true, " ").Length);
        Assert.Empty(ej.Snapshot(false));
    }

    [Fact]
    public void LoggingJournal_KeepsTheEntryAndWritesALogLine()
    {
        var inner = new EventJournal();
        var console = new StringWriter();
        var log = new Log(null, console, () => new LogSettings(true, LogLevel.Info));
        new LoggingJournal(inner, log).Add(new JournalEntry { Kind = "MarkerPosed", PlaylistId = "7", Detail = "family=remove-si-lu cause=first-detection" });

        Assert.Single(inner.Snapshot());
        Assert.Equal("[EmbySharedPlaylist] MarkerPosed playlist=7 family=remove-si-lu cause=first-detection" + Environment.NewLine, console.ToString());
    }

    [Theory]
    [InlineData("ScanPass", null, null, "playlists=3 shared=2", "EmbySharedPlaylist : ScanPass playlists=3 shared=2")]
    [InlineData("Removal", "7", "5", "entries=1 durationMs=12", "EmbySharedPlaylist : Removal playlist=7 item=5 user=- entries=1 durationMs=12")]
    [InlineData("Skipped", "7", null, "already-seen", "EmbySharedPlaylist : Skipped playlist=7 already-seen")]
    [InlineData("Error", "7", null, "InvalidOperationException", "EmbySharedPlaylist : Error playlist=7 InvalidOperationException")]
    public void LogFormat_Entry_UsesIdsAndDetailOnly(string kind, string? playlist, string? item, string detail, string expected) =>
        Assert.Equal(expected, LogFormat.Entry(new JournalEntry { Kind = kind, PlaylistId = playlist, ItemId = item, Detail = detail }));

    [Fact]
    public void ApplyResult_SupportsInitializerAndConstructor()
    {
        var a = new ApplyResult { Posed = new[] { MarkerFamily.RemoveSiLu }, OverviewWritten = true };
        var b = new ApplyResult(new[] { MarkerFamily.PropagerLu }, false);
        Assert.True(a.OverviewWritten);
        Assert.Equal(MarkerFamily.PropagerLu, b.Posed.Single());
        Assert.Empty(new ApplyResult().Posed);
    }
}

public class ConfigurationDevTests
{
    [Fact]
    public void Defaults_MatchTheV020Contract()
    {
        var c = new PluginConfiguration();
        Assert.Equal(2, c.GracePasses);
        Assert.True(c.EnableDiagnostics);
        Assert.True(c.LogToConsole);
        Assert.Equal(LogLevel.Info, c.LogLevel);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(10, 10)]
    public void EffectiveGracePasses_HasAMinimumOfOne(int configured, int expected) =>
        Assert.Equal(expected, new PluginConfiguration { GracePasses = configured }.EffectiveGracePasses);
}
