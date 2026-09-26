using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Emby;
using EmbySharedPlaylist.Reconciliation;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// La suspension du moteur (sonde U11 active) couvre TOUS ses chemins d'écriture : passe de démarrage et passe planifiée
/// (ReconciliationService/Tâche), première détection sur événement (FirstDetectionCoordinator/écouteur), DefaultsService
/// (unique point d'écriture des étiquettes) et la passerelle (dernier garde-fou, non testable ici : SDK).
/// </summary>
public class SuspensionDevTests
{
    private sealed class Rig
    {
        public readonly FakeGateway Gateway = new();
        public readonly SeenPlaylists Seen = new();
        public readonly PlaylistLocks Locks = new();
        public readonly ListJournal Journal = new();
        public bool Suspended = true;
        public readonly DefaultsService Defaults;
        public readonly ReconciliationService Reconciliation;
        public readonly FirstDetectionCoordinator Coordinator;

        public Rig()
        {
            Defaults = new DefaultsService(Gateway, Seen, Locks, Journal, "AIDE", () => 2, null, TimeSpan.FromMilliseconds(150), () => Suspended);
            Reconciliation = new ReconciliationService(Gateway, Defaults, Seen, Locks, Journal, new FakeClock(), TimeSpan.FromMilliseconds(150), () => Suspended);
            Coordinator = new FirstDetectionCoordinator(Gateway, Defaults, Seen, Journal, null, () => Suspended);
            Gateway.Add("1");
        }

        public void AssertUntouched()
        {
            Assert.Equal(0, Gateway.ApplyCalls);
            Assert.Equal(0, Gateway.GetCalls);
            Assert.Equal(0, Gateway.ListCalls);
            Assert.False(Seen.IsSeen("1"));
            Assert.Empty(Gateway.Playlists["1"].Tags);
            Assert.Null(Gateway.Playlists["1"].Overview);
        }
    }

    [Fact]
    public void StartupPassAndScheduledPass_DoNothingWhenSuspended()
    {
        var r = new Rig();
        var first = r.Reconciliation.RunPass();   // passe de démarrage
        var second = r.Reconciliation.RunPass();  // passe planifiée
        Assert.Equal(new PassResult(0, 0, 0, 0, 0), first);
        Assert.Equal(new PassResult(0, 0, 0, 0, 0), second);
        Assert.Null(r.Reconciliation.LastPass);
        r.AssertUntouched();
        Assert.All(r.Journal.Entries, e => Assert.Equal("Skipped", e.Kind));
        Assert.All(r.Journal.Details("Skipped"), d => Assert.Equal("suspended", d));
    }

    [Fact]
    public void FirstDetectionOnEvent_DoesNothingWhenSuspended()
    {
        var r = new Rig();
        r.Coordinator.OnPlaylistEvent("1");
        r.AssertUntouched();
        Assert.Empty(r.Journal.Entries);
    }

    [Fact]
    public void DefaultsService_IsTheSingleWritePoint_AndRefusesBothPaths()
    {
        var r = new Rig();
        var snap = new PlaylistSnapshot("1", "o", new[] { "o", "m" }, Array.Empty<string>(), null);
        Assert.True(r.Defaults.OnFirstDetection(snap).Skipped);
        Assert.True(r.Defaults.OnPass(snap).Skipped);
        r.AssertUntouched();
    }

    [Fact]
    public void WhenTheSuspensionEnds_TheEngineWorksAgain_NothingWasMarkedSeenMeanwhile()
    {
        var r = new Rig();
        r.Reconciliation.RunPass();
        r.Coordinator.OnPlaylistEvent("1");
        r.Suspended = false;
        var result = r.Reconciliation.RunPass();
        Assert.Equal(2, result.Posed);
        Assert.True(r.Seen.IsSeen("1"));
    }

    [Fact]
    public void SuspensionIsReadOnEveryCall_NotCapturedAtConstruction()
    {
        var r = new Rig();
        r.Suspended = false;
        r.Reconciliation.RunPass();
        Assert.True(r.Gateway.ApplyCalls > 0);
        r.Suspended = true;
        var calls = r.Gateway.ApplyCalls;
        r.Gateway.Playlists["1"].Tags.Clear();
        r.Reconciliation.RunPass();
        r.Coordinator.OnPlaylistEvent("1");
        Assert.Equal(calls, r.Gateway.ApplyCalls);
    }

    [Fact]
    public void IsSuspended_IsFailClosed_WhenTheConfigurationIsUnavailable()
    {
        Assert.True(PluginRuntime.IsSuspended(null));
        Assert.True(PluginRuntime.IsSuspended(new PluginConfiguration { EnableReentrancyProbe = true }));
        Assert.False(PluginRuntime.IsSuspended(new PluginConfiguration { EnableReentrancyProbe = false }));
        Assert.False(PluginRuntime.IsSuspended(new PluginConfiguration()));
    }
}
