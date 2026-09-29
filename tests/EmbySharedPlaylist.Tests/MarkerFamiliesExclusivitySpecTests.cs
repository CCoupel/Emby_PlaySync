using EmbySharedPlaylist.Marker;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// Tests de SPÉCIFICATION de v1.2.0 (#56, D21, tâche B1 du plan `_work/reports/planner-20260929-123201.md`) : TROIS familles
/// d'étiquettes, chacune ne reconnaît QUE ses propres étiquettes. Risque explicite du plan (§4) : <c>Pattern()</c> et
/// <c>Evaluate()</c> étaient des ternaires à deux branches ; sans <c>switch</c> exhaustif, la 3e famille tomberait
/// silencieusement sur le motif de <c>propager-lu</c>. Ces tests échouent dans ce cas.
/// </summary>
public class MarkerFamiliesExclusivitySpecTests
{
    private static readonly MarkerFamily[] All =
        { MarkerFamily.RemoveSiLu, MarkerFamily.PropagerLu, MarkerFamily.PropagerAvancement };

    public static IEnumerable<object[]> Pairs() =>
        from a in All from b in All where a != b select new object[] { a, b };

    [Fact]
    public void ThereAreExactlyThreeFamilies_WithDistinctNames()
    {
        Assert.Equal(3, Enum.GetValues<MarkerFamily>().Length);
        Assert.Equal(3, All.Select(MarkerEvaluator.FamilyName).Distinct().Count());
        Assert.Equal(new[] { "remove-si-lu", "propager-lu", "propager-avancement" }, All.Select(MarkerEvaluator.FamilyName));
    }

    [Theory, MemberData(nameof(Pairs))]
    public void AFamily_NeverRecognisesTheTagsOfAnotherFamily(MarkerFamily owner, MarkerFamily other)
    {
        foreach (var value in new[] { "OUI", "NON" })
        {
            var tag = MarkerEvaluator.FamilyName(other) + "=" + value;
            Assert.Equal(MarkerState.None, MarkerEvaluator.Evaluate(new[] { tag }, owner));
            Assert.False(MarkerEvaluator.IsActive(new[] { tag }, owner));
        }
    }

    [Fact]
    public void PropagerAvancementTags_AreNotSeenAsPropagerLu_TheSilentFallbackBug()
    {
        var tags = new[] { "propager-avancement=OUI" };
        Assert.Equal(MarkerState.Oui, MarkerEvaluator.Evaluate(tags, MarkerFamily.PropagerAvancement));
        Assert.Equal(MarkerState.None, MarkerEvaluator.Evaluate(tags, MarkerFamily.PropagerLu));
        Assert.Equal(MarkerState.None, MarkerEvaluator.Evaluate(tags, MarkerFamily.RemoveSiLu));
    }

    [Fact]
    public void PropagerLuTags_AreNotSeenAsPropagerAvancement()
    {
        var tags = new[] { "propager-lu=OUI", "propager-lu=NON" };
        Assert.Equal(MarkerState.Both, MarkerEvaluator.Evaluate(tags, MarkerFamily.PropagerLu));
        Assert.Equal(MarkerState.None, MarkerEvaluator.Evaluate(tags, MarkerFamily.PropagerAvancement));
    }

    [Theory]
    [InlineData("propager-avancement=oui", MarkerState.Oui)]
    [InlineData("PROPAGER-AVANCEMENT=Non", MarkerState.Non)]
    [InlineData("  propager-avancement = OUI  ", MarkerState.Oui)]
    [InlineData("propager-avancement=OUIX", MarkerState.None)]
    [InlineData("propager-avancement-oui", MarkerState.None)]
    [InlineData("propager-avancement=", MarkerState.None)]
    [InlineData("propager-avancement", MarkerState.None)]
    [InlineData("xpropager-avancement=OUI", MarkerState.None)]
    [InlineData("propager_avancement=OUI", MarkerState.None)]
    [InlineData("propager-avancement=OUI\nX", MarkerState.None)]
    public void PropagerAvancement_HasTheSameRulesAsTheOtherFamilies(string tag, MarkerState expected) =>
        Assert.Equal(expected, MarkerEvaluator.Evaluate(new[] { tag }, MarkerFamily.PropagerAvancement));

    [Fact]
    public void PropagerAvancement_OuiPlusNon_IsBoth_AndInactive_NonWins()
    {
        var tags = new[] { "propager-avancement=OUI", "propager-avancement=NON" };
        Assert.Equal(MarkerState.Both, MarkerEvaluator.Evaluate(tags, MarkerFamily.PropagerAvancement));
        Assert.False(MarkerEvaluator.IsActive(tags, MarkerFamily.PropagerAvancement));
    }

    [Fact]
    public void ThreeFamiliesInOneTagList_EachIsEvaluatedIndependently()
    {
        var tags = new[] { "remove-si-lu=OUI", "propager-lu=NON", "propager-avancement=OUI", "famille" };
        Assert.Equal(MarkerState.Oui, MarkerEvaluator.Evaluate(tags, MarkerFamily.RemoveSiLu));
        Assert.Equal(MarkerState.Non, MarkerEvaluator.Evaluate(tags, MarkerFamily.PropagerLu));
        Assert.Equal(MarkerState.Oui, MarkerEvaluator.Evaluate(tags, MarkerFamily.PropagerAvancement));
    }
}
