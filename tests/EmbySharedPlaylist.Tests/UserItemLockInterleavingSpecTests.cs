using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Engine;
using EmbySharedPlaylist.Reconciliation;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// Tests de SPÉCIFICATION v1.2.0 (#57, D21 §5, tâche B7) : verrou par couple (utilisateur, média) — <see cref="UserItemLocks"/>,
/// même patron que <see cref="PlaylistLocks"/> — et ENTRELACEMENTS des deux flux d'événements (le lu, R4b, et la position,
/// R10 ; sans ordre garanti) sur la donnée d'un MÊME membre : ni le lu ni la position ne doivent être perdus. Le verrou est le
/// plus interne (toujours pris sous le verrou de playlist), jamais imbriqué avec lui-même ; la relecture de la donnée,
/// l'enregistrement anti-écho et l'écriture se font dans ce verrou (1 écriture &lt;-&gt; 1 écho).
/// Contrat testé : <c>UserItemLocks.TryAcquire(userId, itemId, timeout)</c> (null si délai dépassé), <c>IsHeldByCurrentThread</c>,
/// et le paramètre optionnel <c>userItemLocks</c> des constructeurs de <see cref="ReadRemovalEngine"/>/<see cref="PlaybackPositionEngine"/>.
/// </summary>
public class UserItemLockInterleavingSpecTests
{
    private const long Min = 600_000_000L;
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(5);

    // ================================================================= UserItemLocks (même contrat que PlaylistLocks)

    [Fact]
    public void TryAcquire_ExcludesTwoThreadsOnTheSamePair()
    {
        var locks = new UserItemLocks();
        var inside = 0;
        var maxInside = 0;
        var counter = 0;

        void Work()
        {
            for (var i = 0; i < 200; i++)
            {
                using var l = locks.TryAcquire("u1", "m1", Long);
                Assert.NotNull(l);
                var now = Interlocked.Increment(ref inside);
                if (now > maxInside) maxInside = now;
                counter++;   // non atomique : ne serait pas exact sans exclusion mutuelle
                Interlocked.Decrement(ref inside);
            }
        }

        var threads = Enumerable.Range(0, 4).Select(_ => new Thread(Work)).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        Assert.Equal(1, maxInside);
        Assert.Equal(800, counter);
    }

    [Fact]
    public void DifferentPairs_DoNotBlockEachOther()
    {
        var locks = new UserItemLocks();
        using var a = locks.TryAcquire("u1", "m1", Short);
        Assert.NotNull(a);
        Assert.True(AcquireFromOtherThread(locks, "u1", "m2", Short));   // même utilisateur, autre média
        Assert.True(AcquireFromOtherThread(locks, "u2", "m1", Short));   // autre utilisateur, même média
        Assert.False(AcquireFromOtherThread(locks, "u1", "m1", Short));  // même couple : bloqué
    }

    [Fact]
    public void TryAcquire_IsReentrantOnTheSameThread_AndReleasedOnlyAfterTheLastDispose()
    {
        var locks = new UserItemLocks();
        var outer = locks.TryAcquire("u1", "m1", Short)!;
        var inner = locks.TryAcquire("u1", "m1", Short)!;
        Assert.NotNull(inner);
        Assert.True(locks.IsHeldByCurrentThread("u1", "m1"));

        inner.Dispose();
        Assert.False(AcquireFromOtherThread(locks, "u1", "m1", Short));   // niveau externe toujours détenu
        outer.Dispose();
        Assert.True(AcquireFromOtherThread(locks, "u1", "m1", Long));
        Assert.False(locks.IsHeldByCurrentThread("u1", "m1"));
    }

    [Fact]
    public void ATimeoutReturnsNull_NeverBlocksIndefinitely()
    {
        var locks = new UserItemLocks();
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() => { using var g = locks.TryAcquire("u1", "m1", Long); held.Set(); release.Wait(Long); });
        Assert.True(held.Wait(Long));
        var started = DateTime.UtcNow;
        Assert.Null(locks.TryAcquire("u1", "m1", TimeSpan.FromMilliseconds(100)));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
        release.Set();
        holder.Wait();
    }

    [Fact]
    public void AThread_CannotHoldTwoDifferentPairs_ProgrammingError()
    {
        var locks = new UserItemLocks();
        using var a = locks.TryAcquire("u1", "m1", Short);
        Assert.Throws<InvalidOperationException>(() => locks.TryAcquire("u1", "m2", Short));
        Assert.Throws<InvalidOperationException>(() => locks.TryAcquire("u2", "m1", Short));
    }

    [Theory]
    [InlineData("", "m1")]
    [InlineData("u1", "")]
    [InlineData(null, "m1")]
    [InlineData("u1", null)]
    public void TryAcquire_RejectsEmptyOrNullIds(string? userId, string? itemId) =>
        Assert.ThrowsAny<ArgumentException>(() => new UserItemLocks().TryAcquire(userId!, itemId!, Short));

    [Fact]
    public void TheKey_DoesNotConfuseUserAndItemBoundaries()
    {
        // ("ab","c") et ("a","bc") sont deux couples distincts (pas de collision par simple concaténation).
        var locks = new UserItemLocks();
        using var a = locks.TryAcquire("ab", "c", Short);
        Assert.True(AcquireFromOtherThread(locks, "a", "bc", Short));
    }

    // ================================================================= Entrelacements moteur du lu / moteur de la position

    /// <summary>Données utilisateur PAR COUPLE (utilisateur, média) : un enregistrement (Played + Position) avec un cycle
    /// lecture-modification-écriture NON atomique (pause entre la lecture et l'écriture). Sans sérialisation par le verrou
    /// (utilisateur, média), une écriture écrase l'autre (perte du lu ou de la position).</summary>
    private sealed class OneRecordUserDataGateway : IUserDataGateway
    {
        private sealed class Rec { public bool Played; public long Position; }
        private readonly object _gate = new();
        private readonly Dictionary<(string, string), Rec> _records = new();
        public readonly List<(string What, bool UserItemLockHeld, bool PlaylistLockHeld)> Writes = new();
        public UserItemLocks? UserItemLocks;
        public PlaylistLocks? PlaylistLocks;
        public string? PlaylistId;

        private Rec Get(string userId, string itemId)
        {
            if (!_records.TryGetValue((userId, itemId), out var rec)) _records[(userId, itemId)] = rec = new Rec();
            return rec;
        }

        public bool PlayedOf(string userId, string itemId) { lock (_gate) return Get(userId, itemId).Played; }
        public long PositionOf(string userId, string itemId) { lock (_gate) return Get(userId, itemId).Position; }

        public bool HasAccess(string userId, string itemId) => true;
        public bool? IsPlayed(string userId, string itemId) => PlayedOf(userId, itemId);
        public long? GetPosition(string userId, string itemId) => PositionOf(userId, itemId);

        public bool MarkPlayed(string userId, string itemId)
        {
            long pos;
            lock (_gate) pos = Get(userId, itemId).Position;                                  // lecture
            Thread.Sleep(15);                                                                  // fenêtre d'entrelacement
            lock (_gate) { var rec = Get(userId, itemId); rec.Played = true; rec.Position = pos; }   // écrit TOUT l'enregistrement (position relue plus tôt)
            Track("lu", userId, itemId);
            return true;
        }

        public bool SetPosition(string userId, string itemId, long ticks)
        {
            bool played;
            lock (_gate) played = Get(userId, itemId).Played;
            Thread.Sleep(15);
            lock (_gate) { var rec = Get(userId, itemId); rec.Played = played; rec.Position = ticks; }   // écrit TOUT l'enregistrement (lu relu plus tôt)
            Track("position", userId, itemId);
            return true;
        }

        private void Track(string what, string userId, string itemId)
        {
            var mine = UserItemLocks?.IsHeldByCurrentThread(userId, itemId) ?? false;
            var playlist = PlaylistId != null && (PlaylistLocks?.IsHeldByCurrentThread(PlaylistId) ?? false);
            lock (Writes) Writes.Add((what, mine, playlist));
        }
    }

    /// <summary>Playlist « p » (média « m1 ») dont les membres sont « t1 » (déclencheur du lu), « t2 » (déclencheur de la
    /// position) et « v » (le membre observé, destinataire des DEUX propagations).</summary>
    private sealed class Rig
    {
        public readonly FakeGateway Gateway = new();
        public readonly OneRecordUserDataGateway UserData = new();
        public readonly PluginWriteTracker WriteTracker = new();
        public readonly SeenPlaylists Seen = new();
        public readonly PlaylistLocks Locks = new();
        public readonly UserItemLocks UserItemLocks = new();
        public readonly ListJournal Journal = new();
        public readonly ReadRemovalEngine ReadEngine;
        public readonly PlaybackPositionEngine PositionEngine;

        public Rig(string tags, TimeSpan? timeout = null)
        {
            var clock = new FakeClock();
            var lockTimeout = timeout ?? TimeSpan.FromSeconds(3);
            var defaults = new DefaultsService(Gateway, Seen, Locks, Journal, "AIDE", () => 2, clock, lockTimeout);
            var s = Gateway.Add("p", tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            s.Overview = "déjà";
            s.Items = new List<string> { "m1" };
            s.Members = new List<string> { "t1", "t2", "v" };
            Seen.TryMarkSeen("p");
            UserData.UserItemLocks = UserItemLocks;
            UserData.PlaylistLocks = Locks;
            UserData.PlaylistId = "p";
            ReadEngine = new ReadRemovalEngine(Gateway, UserData, WriteTracker, defaults, Seen, Locks, Journal, clock, lockTimeout, userItemLocks: UserItemLocks);
            PositionEngine = new PlaybackPositionEngine(Gateway, UserData, WriteTracker, defaults, Seen, Locks, Journal, clock, lockTimeout, userItemLocks: UserItemLocks);
        }
    }

    [Fact]
    public void ConcurrentReadAndPositionPropagation_ToTheSameMember_NeitherThePlayedFlagNorThePositionIsLost()
    {
        // Le membre « v » reçoit, en parallèle et sans ordre garanti, le lu propagé (R4b) ET la position (R10). Les deux
        // écritures doivent se composer : lu=vrai ET position=celle de l'arrêt, dans TOUS les ordres d'exécution.
        for (var iteration = 0; iteration < 12; iteration++)
        {
            var r = new Rig("propager-lu=OUI,propager-avancement=OUI,remove-si-lu=NON");
            Parallel.Invoke(
                () => r.ReadEngine.Handle("t1", "m1"),
                () => r.PositionEngine.Handle("t2", "m1", 40 * Min));

            Assert.True(r.UserData.PlayedOf("v", "m1"), $"lu perdu (itération {iteration})");
            Assert.Equal(40 * Min, r.UserData.PositionOf("v", "m1"));   // position perdue sinon
            Assert.Empty(r.Journal.Of("Error"));
        }
    }

    [Fact]
    public void ManyConcurrentPositionAndReadWrites_OnOnePair_ConvergeToPlayedAndAWrittenPosition()
    {
        var r = new Rig("propager-lu=OUI,propager-avancement=OUI");
        var tasks = new List<Task>();
        for (var i = 1; i <= 6; i++)
        {
            var ticks = i * 10 * Min;
            tasks.Add(Task.Run(() => r.PositionEngine.Handle("t2", "m1", ticks)));
            tasks.Add(Task.Run(() => r.ReadEngine.Handle("t1", "m1")));
        }
        Task.WaitAll(tasks.ToArray());

        Assert.True(r.UserData.PlayedOf("v", "m1"));
        Assert.Contains(r.UserData.PositionOf("v", "m1"), Enumerable.Range(1, 6).Select(i => i * 10 * Min));   // une valeur réellement écrite, jamais un état incohérent
        Assert.Empty(r.Journal.Of("Error"));
    }

    [Fact]
    public void EveryWrite_RunsUnderTheUserItemLock_ItselfUnderThePlaylistLock_TheInnermost()
    {
        var r = new Rig("propager-lu=OUI,propager-avancement=OUI");
        r.ReadEngine.Handle("t1", "m1");
        r.PositionEngine.Handle("t2", "m1", 5 * Min);

        Assert.Contains(r.UserData.Writes, w => w.What == "lu");
        Assert.Contains(r.UserData.Writes, w => w.What == "position");
        Assert.All(r.UserData.Writes, w =>
        {
            Assert.True(w.UserItemLockHeld, $"écriture « {w.What} » hors du verrou (utilisateur, média)");
            Assert.True(w.PlaylistLockHeld, $"écriture « {w.What} » hors du verrou de playlist (le verrou (utilisateur, média) est le plus interne)");
        });
        Assert.False(r.UserItemLocks.IsHeldByCurrentThread("v", "m1"));   // libéré après le traitement
        Assert.False(r.Locks.IsHeldByCurrentThread("p"));
    }

    [Fact]
    public void WhenTheUserItemLockIsBusy_ThatMemberIsSkipped_lockBusy_NoWrite_OthersStillProcessed_NoDanglingEcho()
    {
        var r = new Rig("propager-avancement=OUI", TimeSpan.FromMilliseconds(100));
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            using var g = r.UserItemLocks.TryAcquire("v", "m1", Long);   // verrou lié au fil : pris et libéré dans la même tâche
            held.Set();
            release.Wait(Long);
        });
        Assert.True(held.Wait(Long));

        var result = r.PositionEngine.Handle("t2", "m1", 10 * Min);
        release.Set();
        holder.Wait();

        Assert.Equal(1, result.Propagated);                                                  // « t1 » traité
        Assert.Equal(10 * Min, r.UserData.PositionOf("t1", "m1"));
        Assert.Equal(0L, r.UserData.PositionOf("v", "m1"));                                  // « v » intact : verrou occupé
        Assert.Equal(1, r.Journal.Of("Skipped").Count(e => e.Detail == "lock-busy" && e.UserId == "v" && e.ItemId == "m1"));
        Assert.Equal(1, r.WriteTracker.Count);                                               // seule l'écriture de « t1 » attend son écho
        Assert.Empty(r.Journal.Of("Error"));
    }

    [Fact]
    public void TheLockedMembersOwnData_IsNeverWritten_WhileTheLockIsBusy_ReadFlow()
    {
        var r = new Rig("propager-lu=OUI", TimeSpan.FromMilliseconds(100));
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() => { using var g = r.UserItemLocks.TryAcquire("v", "m1", Long); held.Set(); release.Wait(Long); });
        Assert.True(held.Wait(Long));

        r.ReadEngine.Handle("t1", "m1");
        release.Set();
        holder.Wait();

        Assert.False(r.UserData.PlayedOf("v", "m1"));                                        // aucune écriture tant que le verrou est pris ailleurs
        Assert.True(r.UserData.PlayedOf("t2", "m1"));                                        // les autres membres sont traités
        Assert.Contains(r.Journal.Of("Skipped"), e => e.Detail == "lock-busy" && e.UserId == "v");
        Assert.Equal(1, r.WriteTracker.Count);                                               // seul « t2 » attend un écho
    }

    private static bool AcquireFromOtherThread(UserItemLocks locks, string userId, string itemId, TimeSpan timeout)
    {
        var acquired = false;
        var t = new Thread(() =>
        {
            using var l = locks.TryAcquire(userId, itemId, timeout);
            acquired = l != null;
        });
        t.Start();
        t.Join();
        return acquired;
    }
}
