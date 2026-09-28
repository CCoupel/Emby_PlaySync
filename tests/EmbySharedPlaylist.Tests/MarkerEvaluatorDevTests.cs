using EmbySharedPlaylist.Marker;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>Table de vérité des deux familles (tests internes de dev-plugin ; la spécification est couverte par test-writer).</summary>
public class MarkerEvaluatorDevTests
{
    public static IEnumerable<object[]> Families() { yield return new object[] { MarkerFamily.RemoveSiLu }; yield return new object[] { MarkerFamily.PropagerLu }; }

    private static string N(MarkerFamily f) => MarkerEvaluator.FamilyName(f);

    [Fact]
    public void Names_AreTheAgreedTags()
    {
        Assert.Equal("remove-si-lu", MarkerEvaluator.FamilyName(MarkerFamily.RemoveSiLu));
        Assert.Equal("propager-lu", MarkerEvaluator.FamilyName(MarkerFamily.PropagerLu));
        Assert.Equal("remove-si-lu=NON", MarkerEvaluator.NonTag(MarkerFamily.RemoveSiLu));
        Assert.Equal("propager-lu=NON", MarkerEvaluator.NonTag(MarkerFamily.PropagerLu));
    }

    [Theory, MemberData(nameof(Families))]
    public void NoTag_IsNone(MarkerFamily f)
    {
        Assert.Equal(MarkerState.None, MarkerEvaluator.Evaluate(null, f));
        Assert.Equal(MarkerState.None, MarkerEvaluator.Evaluate(new string[0], f));
        Assert.Equal(MarkerState.None, MarkerEvaluator.Evaluate(new[] { "famille", "noel" }, f));
    }

    [Theory, MemberData(nameof(Families))]
    public void NonAlone_IsNon(MarkerFamily f) =>
        Assert.Equal(MarkerState.Non, MarkerEvaluator.Evaluate(new[] { N(f) + "=NON" }, f));

    [Theory, MemberData(nameof(Families))]
    public void OuiAlone_IsOuiAndActive(MarkerFamily f)
    {
        Assert.Equal(MarkerState.Oui, MarkerEvaluator.Evaluate(new[] { N(f) + "=OUI" }, f));
        Assert.True(MarkerEvaluator.IsActive(new[] { N(f) + "=OUI" }, f));
    }

    [Theory, MemberData(nameof(Families))]
    public void OuiAndNon_IsBoth_AndInactive_InEitherOrder(MarkerFamily f)
    {
        Assert.Equal(MarkerState.Both, MarkerEvaluator.Evaluate(new[] { N(f) + "=OUI", N(f) + "=NON" }, f));
        Assert.Equal(MarkerState.Both, MarkerEvaluator.Evaluate(new[] { N(f) + "=NON", N(f) + "=OUI" }, f));
        Assert.False(MarkerEvaluator.IsActive(new[] { N(f) + "=OUI", N(f) + "=NON" }, f));
    }

    [Theory, MemberData(nameof(Families))]
    public void NonActive_ForNonNoneAndBoth(MarkerFamily f)
    {
        Assert.False(MarkerEvaluator.IsActive(new[] { N(f) + "=NON" }, f));
        Assert.False(MarkerEvaluator.IsActive(null, f));
        Assert.False(MarkerEvaluator.IsActive(new string[0], f));
    }

    [Theory]
    [InlineData("remove-si-lu=oui", MarkerState.Oui)]
    [InlineData("REMOVE-SI-LU=Oui", MarkerState.Oui)]
    [InlineData("Remove-Si-Lu=non", MarkerState.Non)]
    [InlineData("remove-si-lu = OUI", MarkerState.Oui)]
    [InlineData("remove-si-lu =OUI", MarkerState.Oui)]
    [InlineData("remove-si-lu= NON", MarkerState.Non)]
    [InlineData("  remove-si-lu=OUI  ", MarkerState.Oui)]
    [InlineData("remove-si-lu-oui", MarkerState.None)]
    [InlineData("remove-si-lu=OUIX", MarkerState.None)]
    [InlineData("remove-si-lu=NONE", MarkerState.None)]
    [InlineData("xremove-si-lu=OUI", MarkerState.None)]
    [InlineData("remove-si-lu=", MarkerState.None)]
    [InlineData("remove-si-lu", MarkerState.None)]
    [InlineData("remove-si-lu=OUI=NON", MarkerState.None)]
    [InlineData("remove_si_lu=OUI", MarkerState.None)]
    [InlineData("", MarkerState.None)]
    [InlineData("remove-si-lu=OUI\n", MarkerState.Oui)]   // espaces blancs de bordure tolérés (retour à la ligne compris)
    [InlineData("remove-si-lu=OUI\nX", MarkerState.None)]
    [InlineData("remove-si-lu=OUI x", MarkerState.None)]
    [InlineData("remove-si-lu=OUI\t", MarkerState.Oui)]
    public void CaseSpacesAndNeighbourVariants(string tag, MarkerState expected) =>
        Assert.Equal(expected, MarkerEvaluator.Evaluate(new[] { tag }, MarkerFamily.RemoveSiLu));

    [Fact]
    public void Families_AreIndependent()
    {
        var tags = new[] { "remove-si-lu=OUI", "propager-lu=NON" };
        Assert.Equal(MarkerState.Oui, MarkerEvaluator.Evaluate(tags, MarkerFamily.RemoveSiLu));
        Assert.Equal(MarkerState.Non, MarkerEvaluator.Evaluate(tags, MarkerFamily.PropagerLu));

        var onlyPropager = new[] { "propager-lu=OUI" };
        Assert.Equal(MarkerState.None, MarkerEvaluator.Evaluate(onlyPropager, MarkerFamily.RemoveSiLu));
        Assert.False(MarkerEvaluator.IsActive(onlyPropager, MarkerFamily.RemoveSiLu));
        Assert.True(MarkerEvaluator.IsActive(onlyPropager, MarkerFamily.PropagerLu));
    }

    [Fact]
    public void ForeignTagsAndNulls_AreIgnored()
    {
        var tags = new string?[] { null, "famille", "remove-si-lu=OUI", "noel" };
        Assert.Equal(MarkerState.Oui, MarkerEvaluator.Evaluate(tags!, MarkerFamily.RemoveSiLu));
    }

    [Fact]
    public void DuplicatesOfTheSameValue_StayOui()
    {
        Assert.Equal(MarkerState.Oui, MarkerEvaluator.Evaluate(new[] { "remove-si-lu=OUI", "REMOVE-SI-LU=oui" }, MarkerFamily.RemoveSiLu));
    }
}
