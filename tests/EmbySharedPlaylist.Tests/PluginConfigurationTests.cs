using EmbySharedPlaylist;
using Xunit;

namespace EmbySharedPlaylist.Tests;

public class PluginConfigurationTests
{
    [Fact]
    public void Diagnostics_IsEnabledByDefault()
    {
        Assert.True(new PluginConfiguration().EnableDiagnostics);
    }

    [Fact]
    public void AutoEnableSharing_IsEnabledByDefault()
    {
        Assert.True(new PluginConfiguration().AutoEnableSharing);
    }

    [Fact]
    public void AutoEnableSharing_CanBeDisabled()
    {
        Assert.False(new PluginConfiguration { AutoEnableSharing = false }.AutoEnableSharing);
    }
}
