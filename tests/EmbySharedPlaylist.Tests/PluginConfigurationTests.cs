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
}
