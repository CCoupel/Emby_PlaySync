using EmbySharedPlaylist.Core;
using Xunit;

namespace EmbySharedPlaylist.Tests;

public class PlaylistLocksTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(5);

    [Fact]
    public void TryAcquire_ExcludesTwoThreadsOnTheSamePlaylist()
    {
        var locks = new PlaylistLocks();
        var inside = 0;
        var maxInside = 0;
        var counter = 0;

        void Work()
        {
            for (var i = 0; i < 200; i++)
            {
                using var l = locks.TryAcquire("p1", Long);
                Assert.NotNull(l);
                var now = Interlocked.Increment(ref inside);
                if (now > maxInside) maxInside = now;
                counter++; // non atomique : ne serait pas exact sans exclusion mutuelle
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
    public void TryAcquire_IsReentrantOnTheSameThread()
    {
        var locks = new PlaylistLocks();
        using var a = locks.TryAcquire("p1", Short);
        using var b = locks.TryAcquire("p1", Short);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.True(locks.IsHeldByCurrentThread("p1"));
    }

    [Fact]
    public void ReentrantLock_ReleasedOnlyAfterTheLastDispose()
    {
        var locks = new PlaylistLocks();
        var outer = locks.TryAcquire("p1", Short)!;
        var inner = locks.TryAcquire("p1", Short)!;

        inner.Dispose();
        Assert.False(AcquireFromOtherThread(locks, "p1", Short)); // toujours détenu par le niveau externe
        outer.Dispose();
        Assert.True(AcquireFromOtherThread(locks, "p1", Long));
    }

    [Fact]
    public void TryAcquire_ReturnsNullWhenAnotherThreadHoldsTheLock()
    {
        var locks = new PlaylistLocks();
        using var held = locks.TryAcquire("p1", Short);
        Assert.False(AcquireFromOtherThread(locks, "p1", Short));
    }

    [Fact]
    public void DifferentPlaylists_DoNotBlockEachOther()
    {
        var locks = new PlaylistLocks();
        using var held = locks.TryAcquire("p1", Short);
        Assert.True(AcquireFromOtherThread(locks, "p2", Short));
    }

    [Fact]
    public void AThread_CannotHoldTwoDifferentPlaylistLocks()
    {
        var locks = new PlaylistLocks();
        using var a = locks.TryAcquire("p1", Short);
        Assert.Throws<InvalidOperationException>(() => locks.TryAcquire("p2", Short));
    }

    [Fact]
    public void AfterRelease_AThreadCanTakeAnotherPlaylist()
    {
        var locks = new PlaylistLocks();
        locks.TryAcquire("p1", Short)!.Dispose();
        using var b = locks.TryAcquire("p2", Short);
        Assert.NotNull(b);
        Assert.False(locks.IsHeldByCurrentThread("p1"));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var locks = new PlaylistLocks();
        var l = locks.TryAcquire("p1", Short)!;
        l.Dispose();
        l.Dispose(); // ne doit pas libérer une seconde fois (SynchronizationLockException)
        Assert.True(AcquireFromOtherThread(locks, "p1", Long));
    }

    [Fact]
    public void TwoOpposedPairsOfThreads_NeverDeadlock()
    {
        var locks = new PlaylistLocks();
        var done = 0;
        void Loop(string id)
        {
            for (var i = 0; i < 300; i++)
            {
                using var l = locks.TryAcquire(id, Long);
                Assert.NotNull(l);
            }
            Interlocked.Increment(ref done);
        }
        var threads = new[] { "a", "b", "a", "b" }.Select(id => new Thread(() => Loop(id))).ToList();
        threads.ForEach(t => t.Start());
        Assert.All(threads, t => Assert.True(t.Join(TimeSpan.FromSeconds(20))));
        Assert.Equal(4, done);
    }

    [Fact]
    public void TryAcquire_RejectsEmptyId()
    {
        Assert.Throws<ArgumentException>(() => new PlaylistLocks().TryAcquire("", Short));
    }

    private static bool AcquireFromOtherThread(PlaylistLocks locks, string id, TimeSpan timeout)
    {
        var ok = false;
        var t = new Thread(() =>
        {
            using var l = locks.TryAcquire(id, timeout);
            ok = l != null;
        });
        t.Start();
        t.Join();
        return ok;
    }
}
