using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Emby;
using EmbySharedPlaylist.Reconciliation;
using Xunit;

namespace EmbySharedPlaylist.Tests;

public class DiagnosticsMapperDevTests
{
    private static JournalEntry E(string kind, string? playlist = null, string? detail = null) =>
        new() { Ts = "2026-09-26T12:00:00.0000000Z", Kind = kind, PlaylistId = playlist, Detail = detail };

    [Fact]
    public void Journal_WithoutFilter_ReturnsEngineDecisionsOnly()
    {
        var entries = new[] { E("ScanPass"), E("UserDataSaved"), E("MarkerPosed"), E("Probe"), E("MarkerSeen"), E("Removal"), E("Skipped"), E("Error"), E("DescriptionWritten"), E("ItemUpdated") };
        var kinds = DiagnosticsMapper.Journal(entries, null).Select(e => e.Kind).ToList();
        Assert.Equal(new[] { "ScanPass", "MarkerPosed", "MarkerSeen", "Removal", "Skipped", "Error", "DescriptionWritten" }, kinds);
    }

    [Fact]
    public void Journal_WithFilter_ReturnsExactlyTheRequestedKinds_CaseInsensitive()
    {
        var entries = new[] { E("Probe"), E("Removal"), E("UserDataSaved"), E("Skipped") };
        Assert.Equal(new[] { "Probe", "Removal" }, DiagnosticsMapper.Journal(entries, "probe, REMOVAL").Select(e => e.Kind));
        Assert.Empty(DiagnosticsMapper.Journal(entries, "Nope"));
    }

    [Fact]
    public void Journal_KeepsIdsAndDetailOnly_OldestFirst()
    {
        var entries = new[]
        {
            new JournalEntry { Ts = "t1", Kind = "Removal", UserId = "u", ItemId = "i", PlaylistId = "p", Detail = "entries=1 durationMs=3", SaveReason = "x", PositionTicks = 5 },
            E("Skipped", "p", "inactive")
        };
        var dto = DiagnosticsMapper.Journal(entries, null);
        Assert.Equal(("t1", "Removal", "u", "i", "p", "entries=1 durationMs=3"),
            (dto[0].Ts, dto[0].Kind, dto[0].UserId, dto[0].ItemId, dto[0].PlaylistId, dto[0].Detail));
        Assert.Equal("inactive", dto[1].Detail);
    }

    [Fact]
    public void State_IsEmptyAtStartup()
    {
        var s = DiagnosticsMapper.State(new SeenPlaylists(), null, (0, 0, 0), 2);
        Assert.Empty(s.SeenPlaylistIds);
        Assert.Empty(s.GraceCounters);
        Assert.Null(s.LastPass.Ts);
        Assert.Equal(0, s.LastPass.PlaylistsSeen);
        Assert.Equal((0L, 0L, 0L), (s.Handler.Count, s.Handler.LastMs, s.Handler.MaxMs));
        Assert.Equal(2, s.GracePasses);
    }

    [Fact]
    public void State_ReflectsTheMemory_WithAllCounterKeys()
    {
        var seen = new SeenPlaylists();
        seen.TryMarkSeen("1"); seen.TryMarkSeen("2");
        seen.Bump("1", "remove-si-lu");
        seen.Bump("1", "remove-si-lu");
        seen.Bump("2", DefaultsService.DescriptionKey);
        var last = new LastPassInfo(new DateTimeOffset(2026, 9, 26, 12, 30, 0, TimeSpan.Zero), 42, 5, 3);

        var s = DiagnosticsMapper.State(seen, last, (7, 12, 90), 3);

        Assert.Equal(new[] { "1", "2" }, s.SeenPlaylistIds);
        Assert.Equal(new Dictionary<string, int> { ["remove-si-lu"] = 2, ["propager-lu"] = 0, ["description"] = 0 }, s.GraceCounters["1"]);
        Assert.Equal(new Dictionary<string, int> { ["remove-si-lu"] = 0, ["propager-lu"] = 0, ["description"] = 1 }, s.GraceCounters["2"]);
        Assert.Equal("2026-09-26T12:30:00.0000000Z", s.LastPass.Ts);
        Assert.Equal((42L, 5, 3), (s.LastPass.DurationMs, s.LastPass.PlaylistsSeen, s.LastPass.SharedManaged));
        Assert.Equal((7L, 12L, 90L), (s.Handler.Count, s.Handler.LastMs, s.Handler.MaxMs));
        Assert.Equal(3, s.GracePasses);
    }

    [Fact]
    public void State_ExposesNoNameOrSecret_OnlyIdsAndCounters()
    {
        var seen = new SeenPlaylists();
        seen.TryMarkSeen("293845");
        var s = DiagnosticsMapper.State(seen, null, (0, 0, 0), 2);
        var props = typeof(DiagnosticsStateDto).GetProperties().Select(p => p.Name)
            .Concat(typeof(DiagnosticsJournalEntryDto).GetProperties().Select(p => p.Name)).ToList();
        Assert.DoesNotContain(props, n => n.Contains("Name", StringComparison.OrdinalIgnoreCase) || n.Contains("Token", StringComparison.OrdinalIgnoreCase)
                                          || n.Contains("Path", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new[] { "293845" }, s.SeenPlaylistIds);
    }
}
