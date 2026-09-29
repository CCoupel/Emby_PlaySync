using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Engine;
using EmbySharedPlaylist.Reconciliation;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// Investigation I28/I33 (qa-integration-v031-20260927-202137-final.md) : reproduit fidèlement, en mémoire, l'écho
/// SYNCHRONE réel du SDK (<c>SaveUserData</c> → <c>UserDataSaved</c> → <c>PlaybackEventProcessor.Process</c>, MÊME fil)
/// pour une propagation de position, en partageant les MÊMES instances (<c>PluginWriteTracker</c>, <c>PlaylistLocks</c>,
/// <c>SeenPlaylists</c>) entre <c>ReadRemovalEngine</c> et <c>PlaybackPositionEngine</c> — exactement comme
/// <c>PluginRuntime.Initialize</c>. Si ces tests passent, la collision d'état partagé (hypothèse de qa) est écartée
/// avec preuve ; sinon, ils isolent le défaut exact.
/// </summary>
public class PositionEchoChainInvestigationDevTests
{
    /// <summary>Simule <c>EmbyUserDataGateway</c> + la dispatche SYNCHRONE de <c>UserDataSaved</c> (même fil) que le SDK réel effectue.</summary>
    private sealed class EchoingUserDataGateway : IUserDataGateway
    {
        private readonly FakeUserDataGateway _inner;
        private readonly Action<string, string, string, bool> _onSaved;

        public EchoingUserDataGateway(FakeUserDataGateway inner, Action<string, string, string, bool> onSaved)
        {
            _inner = inner;
            _onSaved = onSaved;
        }

        public bool HasAccess(string userId, string itemId) => _inner.HasAccess(userId, itemId);
        public bool? IsPlayed(string userId, string itemId) => _inner.IsPlayed(userId, itemId);
        public long? GetPosition(string userId, string itemId) => _inner.GetPosition(userId, itemId);

        public bool MarkPlayed(string userId, string itemId)
        {
            var wrote = _inner.MarkPlayed(userId, itemId);
            if (wrote) _onSaved(userId, itemId, "TogglePlayed", true); // même UserDataSaveReason qu'EmbyUserDataGateway.MarkPlayed
            return wrote;
        }

        public bool SetPosition(string userId, string itemId, long ticks)
        {
            var wrote = _inner.SetPosition(userId, itemId, ticks);
            if (wrote) _onSaved(userId, itemId, "PlaybackProgress", _inner.IsPlayed(userId, itemId) == true); // Played jamais touché par SetPosition
            return wrote;
        }
    }

    private sealed class Rig
    {
        public readonly FakeGateway Gateway = new();
        public readonly FakeUserDataGateway RawUserData = new();
        public readonly PluginWriteTracker Tracker = new();
        public readonly SeenPlaylists Seen = new();
        public readonly PlaylistLocks Locks = new();
        public readonly ListJournal Journal = new();
        public readonly HandlerStats ReadHandlerStats = new();
        public readonly PlaybackPositionEngine PositionEngine;
        public readonly ReadRemovalEngine ReadEngine;

        public Rig()
        {
            var clock = new FakeClock();
            var defaults = new DefaultsService(Gateway, Seen, Locks, Journal, "AIDE", () => 2, clock, TimeSpan.FromMilliseconds(150));
            var playedTracker = new PlayedTransitionTracker();
            ReadRemovalEngine readEngine = null!;
            var echoing = new EchoingUserDataGateway(RawUserData, (userId, itemId, reason, played) =>
            {
                // Reproduit PlaybackListener.OnUserDataSaved : PluginWriteTracker AVANT tout (ordre A1), même fil, synchrone.
                var processor = new PlaybackEventProcessor(playedTracker, Tracker, (u, i) => readEngine.Handle(u, i), ReadHandlerStats);
                processor.Process(userId, itemId, reason, played);
            });
            ReadEngine = new ReadRemovalEngine(Gateway, echoing, Tracker, defaults, Seen, Locks, Journal, clock, TimeSpan.FromMilliseconds(150));
            readEngine = ReadEngine;
            PositionEngine = new PlaybackPositionEngine(Gateway, echoing, Tracker, defaults, Seen, Locks, Journal, clock, TimeSpan.FromMilliseconds(150));
        }

        public FakeGateway.State AddSharedPlaylist(string id, params string[] members)
        {
            var s = Gateway.Add(id, "propager-lu=OUI", "propager-avancement=OUI"); // v1.2.0 : lu ET avancement, deux familles indépendantes
            s.Overview = "x";
            s.Members = members.ToList();
            s.Items = new List<string> { "m1" };
            Seen.TryMarkSeen(id);
            return s;
        }
    }

    [Fact]
    public void I28_TwoConsecutivePropagationsOnTheSamePlaylist_ReverseDirection_BothSucceed()
    {
        var r = new Rig();
        r.AddSharedPlaylist("P", "u1", "u2");

        // Événement 1 (réel) : u1 arrête -> propagation vers u2 (déclenche un écho SYNCHRONE UserDataSaved(u2, m1), consommé).
        var e1 = r.PositionEngine.Handle("u1", "m1", 300_000_000);
        Assert.Equal(1, e1.Propagated);
        Assert.Equal(300_000_000L, r.RawUserData.GetPosition("u2", "m1"));
        Assert.Single(r.Journal.Of("PositionPropagation"));

        // Événement 2 (réel, immédiatement après, sens inverse) : u2 arrête -> propagation vers u1.
        var e2 = r.PositionEngine.Handle("u2", "m1", 600_000_000);
        Assert.Equal(1, e2.Propagated);
        Assert.Equal(600_000_000L, r.RawUserData.GetPosition("u1", "m1"));
        Assert.Equal(2, r.Journal.Of("PositionPropagation").Count());
    }

    [Fact]
    public void I33_NonTransitivity_TwoPlaylistsSharingTheSameMedia_BothPropagateIndependently()
    {
        var r = new Rig();
        r.AddSharedPlaylist("L1", "u1", "u2");
        r.AddSharedPlaylist("L2", "u1", "u3"); // même média (m1), u1 membre des deux, u2/u3 disjoints

        var e1 = r.PositionEngine.Handle("u2", "m1", 400_000_000); // u2 (L1 seule) arrête -> u1 (L1)
        Assert.Equal(1, e1.Propagated);
        Assert.Equal(400_000_000L, r.RawUserData.GetPosition("u1", "m1"));
        Assert.Equal(0L, r.RawUserData.GetPosition("u3", "m1")); // L2 non touchée (non-transitivité)

        var e2 = r.PositionEngine.Handle("u3", "m1", 700_000_000); // u3 (L2 seule) arrête -> u1 (L2)
        Assert.Equal(1, e2.Propagated);
        Assert.Equal(700_000_000L, r.RawUserData.GetPosition("u1", "m1"));
    }

    [Fact]
    public void ThreeConsecutivePropagations_SamePlaylist_AllSucceed_NoStateLeak()
    {
        var r = new Rig();
        r.AddSharedPlaylist("P", "u1", "u2");

        Assert.Equal(1, r.PositionEngine.Handle("u1", "m1", 100_000_000).Propagated);
        Assert.Equal(1, r.PositionEngine.Handle("u2", "m1", 200_000_000).Propagated);
        Assert.Equal(1, r.PositionEngine.Handle("u1", "m1", 300_000_000).Propagated);
        Assert.Equal(300_000_000L, r.RawUserData.GetPosition("u2", "m1"));
        Assert.Equal(3, r.Journal.Of("PositionPropagation").Count());
    }

    [Fact]
    public void ReadPropagationThenPositionPropagation_OnTheSamePair_BothWork_TrackerNotConfused()
    {
        // Le MÊME PluginWriteTracker sert aux deux moteurs (mêmes clés (userId, itemId)) : vérifie qu'une propagation du
        // lu (#20) suivie d'une propagation de position (#45) sur le MÊME couple ne se bloquent pas mutuellement.
        var r = new Rig();
        r.AddSharedPlaylist("P", "u1", "u2");
        r.RawUserData.SetPlayed("u1", "m1", true); // u1 a déjà lu m1 : le retrait/la propagation du lu se déclenche sur u1

        var readResult = r.ReadEngine.Handle("u1", "m1");
        Assert.True(r.RawUserData.IsPlayed("u2", "m1")); // propagation du lu réussie (#20)

        var positionResult = r.PositionEngine.Handle("u2", "m1", 500_000_000); // u2 (déjà lu) arrête -> propage sa position à u1
        Assert.Equal(1, positionResult.Propagated);
        Assert.Equal(500_000_000L, r.RawUserData.GetPosition("u1", "m1"));
    }
}
