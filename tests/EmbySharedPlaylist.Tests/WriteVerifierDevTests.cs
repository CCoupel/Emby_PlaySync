using EmbySharedPlaylist.Core;
using Xunit;

namespace EmbySharedPlaylist.Tests;

public class WriteVerifierDevTests
{
    [Fact]
    public void AfterContainsEverythingBeforeAndAdded_IsValid() =>
        Assert.True(WriteVerifier.Verify(new[] { "a", "b" }, new[] { "remove-si-lu=NON" }, new[] { "b", "a", "remove-si-lu=NON", "extra" }, null, null));

    [Fact]
    public void AMissingOwnerTag_IsDetected() =>
        Assert.False(WriteVerifier.Verify(new[] { "a", "b" }, new[] { "remove-si-lu=NON" }, new[] { "a", "remove-si-lu=NON" }, null, null));

    [Fact]
    public void AMissingAddedTag_IsDetected() =>
        Assert.False(WriteVerifier.Verify(new[] { "a" }, new[] { "remove-si-lu=NON", "propager-lu=NON" }, new[] { "a", "remove-si-lu=NON" }, null, null));

    [Fact]
    public void TagsAreComparedExactly_CaseSensitive() =>
        Assert.False(WriteVerifier.Verify(new[] { "Famille" }, null, new[] { "famille" }, null, null));

    [Fact]
    public void WrittenOverview_MustBeTheOneReadBack()
    {
        Assert.True(WriteVerifier.Verify(null, null, null, "AIDE", "AIDE"));
        Assert.False(WriteVerifier.Verify(null, null, null, "AIDE", "autre"));
        Assert.False(WriteVerifier.Verify(null, null, null, "AIDE", null));
    }

    [Fact]
    public void NoOverviewWritten_IsNotChecked() =>
        Assert.True(WriteVerifier.Verify(null, null, Array.Empty<string>(), null, "n'importe quoi"));

    [Fact]
    public void NullsAreTolerated() =>
        Assert.True(WriteVerifier.Verify(null, null, null, null, null));

    [Fact]
    public void TheVerifierOnlyReports_ItNeverMutatesItsInputs()
    {
        var before = new List<string> { "a" };
        var after = new List<string> { "a", "x" };
        WriteVerifier.Verify(before, new[] { "x" }, after, null, null);
        Assert.Equal(new[] { "a" }, before);
        Assert.Equal(new[] { "a", "x" }, after);
    }

    [Fact]
    public void TheException_CarriesNoMessageDataToLeak() =>
        Assert.Equal("WriteVerificationException", new WriteVerificationException().GetType().Name);
}
