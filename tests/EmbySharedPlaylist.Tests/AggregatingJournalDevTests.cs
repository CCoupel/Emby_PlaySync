using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Emby;
using Moq;
using MediaBrowser.Model.Logging;
using Xunit;

namespace EmbySharedPlaylist.Tests;

public class AggregatingJournalDevTests
{
    private static JournalEntry Skip(string reason, string playlist = "1") => JournalEntries.SkippedEntry(null, playlist, reason);

    [Theory]
    [InlineData("already-seen")]
    [InlineData("reentrant")]
    [InlineData("not-shared")]
    [InlineData("unknown-owner")]
    public void NoisyReasons_AreCountedButNeverJournaled(string reason)
    {
        var inner = new ListJournal();
        var counters = new SkippedCounters();
        var j = new AggregatingJournal(inner, counters);
        for (var i = 0; i < 100; i++) j.Add(Skip(reason));
        Assert.Empty(inner.Entries);                 // le journal borné n'est pas noyé
        Assert.Equal(100, counters.Snapshot()[reason]);
    }

    [Theory]
    [InlineData("lock-busy")]
    [InlineData("inactive")]
    [InlineData("already-removed")]
    [InlineData("marker-present")]
    [InlineData("suspended")]
    public void InformativeReasons_AreJournaledAndCounted(string reason)
    {
        var inner = new ListJournal();
        var counters = new SkippedCounters();
        new AggregatingJournal(inner, counters).Add(Skip(reason));
        Assert.Equal(new[] { reason }, inner.Details("Skipped"));
        Assert.Equal(1, counters.Snapshot()[reason]);
    }

    [Fact]
    public void OtherKinds_PassThroughUntouched_AndAreNotCounted()
    {
        var inner = new ListJournal();
        var counters = new SkippedCounters();
        var j = new AggregatingJournal(inner, counters);
        j.Add(JournalEntries.Of(null, "MarkerPosed", "1", "family=remove-si-lu cause=first-detection"));
        j.Add(JournalEntries.ErrorEntry(null, "1", new InvalidOperationException("x")));
        Assert.Equal(new[] { "MarkerPosed", "Error" }, inner.Entries.Select(e => e.Kind));
        Assert.Empty(counters.Snapshot());
    }

    [Fact]
    public void NoisyReasons_GoToTheFileAtDebugOnly_NeverInfoOrConsole()
    {
        var logger = new Mock<ILogger>();
        var console = new StringWriter();
        var log = new Log(logger.Object, console, () => new LogSettings(true, LogLevel.Debug));
        new AggregatingJournal(new ListJournal(), new SkippedCounters(), log).Add(Skip("already-seen"));

        logger.Verify(l => l.Debug("{0}", It.IsAny<object[]>()), Times.Once);
        logger.Verify(l => l.Info(It.IsAny<string>(), It.IsAny<object[]>()), Times.Never);
        Assert.Equal(string.Empty, console.ToString());
    }

    [Fact]
    public void NoisyReasons_AreSilentAtInfoLevel()
    {
        var logger = new Mock<ILogger>();
        var log = new Log(logger.Object, new StringWriter(), () => new LogSettings(true, LogLevel.Info));
        new AggregatingJournal(new ListJournal(), new SkippedCounters(), log).Add(Skip("reentrant"));
        logger.Verify(l => l.Debug(It.IsAny<string>(), It.IsAny<object[]>()), Times.Never);
        logger.Verify(l => l.Info(It.IsAny<string>(), It.IsAny<object[]>()), Times.Never);
    }

    [Fact]
    public void Counters_AreThreadSafe()
    {
        var counters = new SkippedCounters();
        var j = new AggregatingJournal(new ListJournal(), counters);
        Parallel.For(0, 1000, i => j.Add(Skip(i % 2 == 0 ? "already-seen" : "reentrant")));
        var s = counters.Snapshot();
        Assert.Equal(500, s["already-seen"]);
        Assert.Equal(500, s["reentrant"]);
    }

    [Fact]
    public void DiagnosticsState_ExposesSkippedCounts()
    {
        var counters = new SkippedCounters();
        counters.Increment("already-seen"); counters.Increment("already-seen"); counters.Increment("lock-busy");
        var s = DiagnosticsMapper.State(new SeenPlaylists(), null, (0, 0, 0), 2, counters.Snapshot());
        Assert.Equal(2, s.SkippedCounts["already-seen"]);
        Assert.Equal(1, s.SkippedCounts["lock-busy"]);
        Assert.Empty(DiagnosticsMapper.State(new SeenPlaylists(), null, (0, 0, 0), 2).SkippedCounts);
    }
}
