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
}
