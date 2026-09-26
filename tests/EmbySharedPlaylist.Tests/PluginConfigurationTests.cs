using EmbySharedPlaylist;
using Xunit;

namespace EmbySharedPlaylist.Tests;

public class PluginConfigurationTests
{
    [Fact]
    public void SpikeEndpoints_AreDisabledByDefault()
    {
        Assert.False(new PluginConfiguration().EnableSpikeEndpoints);
    }

    [Fact]
    public void ReentrancyProbe_IsDisabledByDefault()
    {
        Assert.False(new PluginConfiguration().EnableReentrancyProbe);
    }
}
