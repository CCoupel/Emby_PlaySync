using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Spike;
using Xunit;

namespace EmbySharedPlaylist.Tests;

public class SpikeLogFormatTests
{
    [Fact]
    public void Startup_ContainsFilterTokenAndConfigValue()
    {
        Assert.Equal("EmbySharedPlaylist : écouteurs du spike enregistrés (EnableSpikeEndpoints=true)", LogFormat.Startup(true));
        Assert.Contains("EnableSpikeEndpoints=false", LogFormat.Startup(false));
    }

    [Fact]
    public void UserDataSaved_ListsAllRequestedFields()
    {
        var line = SpikeLogFormat.Event(new JournalEntry
        {
            Kind = "UserDataSaved", UserId = "abc", ItemId = "42", SaveReason = "PlaybackFinished",
            Played = true, PositionTicks = 1234, PluginWrite = false
        });
        Assert.Equal("EmbySharedPlaylist spike : UserDataSaved user=abc item=42 reason=PlaybackFinished played=true pos=1234 pluginWrite=false", line);
    }

    [Fact]
    public void UserDataSaved_MissingValuesAreShownExplicitly()
    {
        var line = SpikeLogFormat.Event(new JournalEntry { Kind = "UserDataSaved" });
        Assert.Equal("EmbySharedPlaylist spike : UserDataSaved user=- item=- reason=- played=null pos=- pluginWrite=false", line);
    }

    [Fact]
    public void PlaylistEvents_ShowPlaylistAndEntry_AddedAlsoShowsItem()
    {
        Assert.Equal("EmbySharedPlaylist spike : PlaylistItemsAdded playlist=7 entry=9 item=5",
            SpikeLogFormat.Event(new JournalEntry { Kind = "PlaylistItemsAdded", PlaylistId = "7", EntryId = "9", ItemId = "5" }));
        Assert.Equal("EmbySharedPlaylist spike : PlaylistItemsRemoved playlist=7 entry=9",
            SpikeLogFormat.Event(new JournalEntry { Kind = "PlaylistItemsRemoved", PlaylistId = "7", EntryId = "9" }));
        Assert.Equal("EmbySharedPlaylist spike : PlaylistItemsMoved playlist=7 entry=9",
            SpikeLogFormat.Event(new JournalEntry { Kind = "PlaylistItemsMoved", PlaylistId = "7", EntryId = "9" }));
    }

    [Fact]
    public void Probe_ShowsPlaylistAndDetail() =>
        Assert.Equal("EmbySharedPlaylist spike : Probe playlist=7 scenario=P1 durationMs=12 lockWaitMs=0 echoes=1 outcome=OK",
            SpikeLogFormat.Event(new JournalEntry { Kind = "Probe", PlaylistId = "7", Detail = "scenario=P1 durationMs=12 lockWaitMs=0 echoes=1 outcome=OK" }));

    [Fact]
    public void ItemUpdated_ShowsPlaylistAndReason() =>
        Assert.Equal("EmbySharedPlaylist spike : ItemUpdated playlist=7 reason=MetadataEdit",
            SpikeLogFormat.Event(new JournalEntry { Kind = "ItemUpdated", PlaylistId = "7", SaveReason = "MetadataEdit" }));

    [Theory]
    [InlineData("UserDataSaved", "PlaybackProgress", true)]
    [InlineData("UserDataSaved", "PlaybackFinished", false)]
    [InlineData("UserDataSaved", "TogglePlayed", false)]
    [InlineData("ItemUpdated", "PlaybackProgress", false)]
    [InlineData("UserDataSaved", null, false)]
    public void IsDebugLevel_OnlyForPlaybackProgress(string kind, string? reason, bool expected) =>
        Assert.Equal(expected, SpikeLogFormat.IsDebugLevel(new JournalEntry { Kind = kind, SaveReason = reason }));

    [Fact]
    public void OperationLines_UseIdsOnlyAndTheFilterPrefix()
    {
        var lines = new[]
        {
            SpikeLogFormat.Setup("1", "guid", 2),
            SpikeLogFormat.Policy("guid", true),
            SpikeLogFormat.MarkPlayed("guid", "3", true, false, true),
            SpikeLogFormat.SetPosition("guid", "3", 100, 100, false),
            SpikeLogFormat.RemoveItem("1", 1, 2),
            SpikeLogFormat.TagsPost("1", 1, 0, false, 3)
        };
        Assert.All(lines, l => Assert.StartsWith("EmbySharedPlaylist", l));
        Assert.Equal("EmbySharedPlaylist spike : Setup playlist=1 owner=guid members=2", lines[0]);
        Assert.Equal("EmbySharedPlaylist spike : Policy user=guid AllowSharingPersonalItems=true", lines[1]);
        Assert.Equal("EmbySharedPlaylist spike : MarkPlayed user=guid item=3 played=true asPlugin=false playedAfter=true", lines[2]);
        Assert.Equal("EmbySharedPlaylist spike : SetPosition user=guid item=3 requested=100 after=100 playedAfter=false", lines[3]);
        Assert.Equal("EmbySharedPlaylist spike : RemoveItem playlist=1 removed=1 entriesAfter=2", lines[4]);
        Assert.Equal("EmbySharedPlaylist spike : Tags playlist=1 add=1 remove=0 overviewChanged=false tagsAfter=3", lines[5]);
    }

    [Fact]
    public void Event_UnknownKind_StillProducesAPrefixedLine() =>
        Assert.StartsWith("EmbySharedPlaylist spike : ", SpikeLogFormat.Event(new JournalEntry { Kind = "X" }));
}
