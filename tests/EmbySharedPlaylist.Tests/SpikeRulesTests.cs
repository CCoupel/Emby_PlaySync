using EmbySharedPlaylist.Spike;
using Xunit;

namespace EmbySharedPlaylist.Tests;

public class SpikeRulesTests
{
    [Theory]
    [InlineData("test_u1", true)]
    [InlineData("test_", true)]
    [InlineData("admin", false)]
    [InlineData("cyril", false)]
    [InlineData("user2", false)]
    [InlineData("Test_u1", false)]
    [InlineData("xtest_u1", false)]
    [InlineData(null, false)]
    public void IsTestUser_OnlyAcceptsTestAccounts(string? name, bool expected) =>
        Assert.Equal(expected, SpikeRules.IsTestUser(name));

    [Theory]
    [InlineData("SPIKE À voir", true)]
    [InlineData("SPIKE-x", true)]
    [InlineData("À voir", false)]
    [InlineData("spike", false)]
    [InlineData(null, false)]
    public void IsSpikePlaylist_OnlyAcceptsSpikeNames(string? name, bool expected) =>
        Assert.Equal(expected, SpikeRules.IsSpikePlaylist(name));

    [Theory]
    [InlineData(null, "SPIKE À voir")]
    [InlineData("  ", "SPIKE À voir")]
    [InlineData("SPIKE-a", "SPIKE-a")]
    [InlineData("Ma liste", "SPIKE-Ma liste")]
    public void EnsureSpikeName_AlwaysProducesSpikeName(string? input, string expected) =>
        Assert.Equal(expected, SpikeRules.EnsureSpikeName(input));

    private static JournalEntry R(string? reason) => new() { SaveReason = reason };

    [Fact]
    public void FilterBySaveReason_EmptyFilterKeepsEverything()
    {
        var all = new[] { R("PlaybackFinished"), R(null) };
        Assert.Equal(2, SpikeRules.FilterBySaveReason(all, null).Length);
        Assert.Equal(2, SpikeRules.FilterBySaveReason(all, " , ").Length);
    }

    [Fact]
    public void FilterBySaveReason_KeepsOnlyListedReasons_CaseInsensitive()
    {
        var all = new[] { R("PlaybackFinished"), R("PlaybackProgress"), R("TogglePlayed"), R(null) };
        var r = SpikeRules.FilterBySaveReason(all, "playbackfinished, PlaybackProgress");
        Assert.Equal(new[] { "PlaybackFinished", "PlaybackProgress" }, r.Select(e => e.SaveReason));
    }

    [Fact]
    public void FilterBySaveReason_UnknownReasonYieldsNothing()
    {
        Assert.Empty(SpikeRules.FilterBySaveReason(new[] { R("PlaybackFinished") }, "Nope"));
    }

    [Fact]
    public void ApplyTagChanges_AddsWithoutTouchingOtherTags()
    {
        var r = SpikeRules.ApplyTagChanges(new[] { "famille", "noel" }, new[] { "propager-lu=NON" }, null);
        Assert.Equal(new[] { "famille", "noel", "propager-lu=NON" }, r);
    }

    [Fact]
    public void ApplyTagChanges_RemovesOnlyNamedTags_CaseInsensitive()
    {
        var r = SpikeRules.ApplyTagChanges(new[] { "famille", "Propager-Lu=NON" }, null, new[] { "propager-lu=non" });
        Assert.Equal(new[] { "famille" }, r);
    }

    [Fact]
    public void ApplyTagChanges_BothLabelsCanCoexist()
    {
        var r = SpikeRules.ApplyTagChanges(new[] { "propager-lu=NON" }, new[] { "propager-lu=OUI" }, null);
        Assert.Equal(new[] { "propager-lu=NON", "propager-lu=OUI" }, r);
    }

    [Fact]
    public void ApplyTagChanges_ReplaceNonByOui_InOneCall()
    {
        var r = SpikeRules.ApplyTagChanges(new[] { "x", "propager-lu=NON" }, new[] { "propager-lu=OUI" }, new[] { "propager-lu=NON" });
        Assert.Equal(new[] { "x", "propager-lu=OUI" }, r);
    }

    [Fact]
    public void ApplyTagChanges_DoesNotDuplicateExistingTag()
    {
        var r = SpikeRules.ApplyTagChanges(new[] { "a" }, new[] { "A", "a" }, null);
        Assert.Equal(new[] { "a" }, r);
    }

    [Fact]
    public void ApplyTagChanges_IgnoresBlankTagsAndNulls()
    {
        Assert.Equal(new[] { "a" }, SpikeRules.ApplyTagChanges(new[] { "a" }, new[] { "", "  " }, null));
        Assert.Empty(SpikeRules.ApplyTagChanges(null, null, null));
    }

    [Fact]
    public void ApplyTagChanges_DoesNotMutateInput()
    {
        var current = new List<string> { "a" };
        SpikeRules.ApplyTagChanges(current, new[] { "b" }, new[] { "a" });
        Assert.Equal(new[] { "a" }, current);
    }
}
