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
    public void AutoEnableSharing_IsDisabledByDefault_SinceV1_0_0_GateProd_M1()
    {
        // v0.4.0-v0.5.0 : défaut vrai. v1.0.0 (GATE PROD, security-20260927-221434.md M1) : défaut faux — un serveur
        // PROD peut être exposé publiquement, un élargissement de droits automatique n'est plus acceptable par défaut.
        Assert.False(new PluginConfiguration().AutoEnableSharing);
    }

    [Fact]
    public void AutoEnableSharing_CanBeEnabled()
    {
        Assert.True(new PluginConfiguration { AutoEnableSharing = true }.AutoEnableSharing);
    }
}
