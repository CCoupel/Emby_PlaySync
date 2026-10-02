using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Engine;
using EmbySharedPlaylist.Marker;
using EmbySharedPlaylist.Reconciliation;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// Réserve de revue M1 (v1.2.2, F1) : le moteur ne doit poser AUCUN WriteScope autour de la boucle de retraits. Un scope externe
/// resterait vivant pendant les attentes de la passerelle (attente de fin de rafraîchissement entre deux RemoveOneEntry) et rendrait
/// « reentrant » un ItemUpdated légitime d'un worker de l'hôte (et ignorerait une première détection). Seule la passerelle ouvre
/// le scope, uniquement autour de son écriture. RED : avec le scope externe de ReadRemovalEngine, Active vaut true à chaque appel.
/// </summary>
public class ReadRemovalEngineScopeSpecTests
{
    [Fact]
    public void NoWriteScopeIsActive_WhenTheEngineCallsTheGateway_ForEveryDuplicateEntry()
    {
        var gateway = new FakeGateway();
        var seen = new SeenPlaylists();
        var locks = new PlaylistLocks();
        var journal = new ListJournal();
        var clock = new FakeClock();
        var timeout = TimeSpan.FromMilliseconds(500);
        var defaults = new DefaultsService(gateway, seen, locks, journal, "AIDE", () => 2, clock, timeout);
        var engine = new ReadRemovalEngine(gateway, new FakeUserDataGateway(), new PluginWriteTracker(), defaults, seen, locks, journal, clock, timeout);

        var s = gateway.Add("p", "remove-si-lu=OUI", "propager-lu=OUI");
        s.Overview = "déjà"; s.Members = new List<string> { "o", "m", "r" };
        s.Items = new List<string> { "x", "y", "x", "x" };
        seen.TryMarkSeen("p");

        var activeAtEachCall = new List<bool>();
        gateway.OnRemove = _ => activeAtEachCall.Add(WriteScope.Active);   // lu AU MOMENT de l'appel (dans le fil du moteur)

        var result = engine.Handle("m", "x");

        Assert.Equal(3, result!.EntriesRemoved);
        Assert.True(activeAtEachCall.Count >= 3);
        Assert.All(activeAtEachCall, active => Assert.False(active));
        Assert.False(WriteScope.Active);
    }
}
