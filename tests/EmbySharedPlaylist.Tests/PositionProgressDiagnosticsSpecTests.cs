using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Emby;
using EmbySharedPlaylist.Reconciliation;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// Tests de SPÉCIFICATION v1.2.1 (#58, D23) du contrat <c>Diagnostics/State.PositionProgress { Propagated, Throttled, LockBusy }</c>
/// (clé AJOUTÉE, rétrocompatible : <c>contracts/http-endpoints.md</c>, <c>contracts/CHANGELOG.md</c> [20260930]) et des compteurs
/// mémoire <see cref="PositionProgressCounters"/> (compteurs des <c>PlaybackProgress</c> périodiques, jamais journalisés un à un).
/// Complète <c>DiagnosticsMapperDevTests</c> (immuable) par un fichier distinct.
/// Contrat supposé (aligné sur le code dev-plugin) : <c>DiagnosticsMapper.State(..., skipped, positionProgress:
/// (Propagated, Throttled, LockBusy)?)</c> ; <c>DiagnosticsStateDto.PositionProgress</c> de type <c>DiagnosticsPositionProgressDto</c> ;
/// <c>PositionProgressCounters.IncrementPropagated/IncrementThrottled/IncrementLockBusy</c> et <c>Snapshot()</c>.
/// </summary>
public class PositionProgressDiagnosticsSpecTests
{
    [Fact]
    public void State_ExposesPositionProgress_AllThreeCounters()
    {
        var s = DiagnosticsMapper.State(new SeenPlaylists(), null, (0, 0, 0), 2, null, (7L, 54L, 2L));

        Assert.Equal((7L, 54L, 2L), (s.PositionProgress.Propagated, s.PositionProgress.Throttled, s.PositionProgress.LockBusy));
    }

    [Fact]
    public void State_PositionProgress_IsPresentAndZeroAtStartup_EvenWhenNotProvided()
    {
        // Clé toujours présente (les tests d'intégration lisent .positionProgress.* sans garde) : 0 par défaut.
        var s = DiagnosticsMapper.State(new SeenPlaylists(), null, (0, 0, 0), 2);

        Assert.NotNull(s.PositionProgress);
        Assert.Equal((0L, 0L, 0L), (s.PositionProgress.Propagated, s.PositionProgress.Throttled, s.PositionProgress.LockBusy));
    }

    [Fact]
    public void State_PositionProgress_DoesNotDisturbTheOtherKeys_Retrocompatible()
    {
        var seen = new SeenPlaylists();
        seen.TryMarkSeen("1");
        var skipped = new Dictionary<string, long> { ["same-position"] = 3 };

        var s = DiagnosticsMapper.State(seen, null, (5, 9, 12), 3, skipped, (1L, 2L, 3L));

        Assert.Equal(new[] { "1" }, s.SeenPlaylistIds);
        Assert.Equal((5L, 9L, 12L), (s.Handler.Count, s.Handler.LastMs, s.Handler.MaxMs));
        Assert.Equal(3, s.GracePasses);
        Assert.Equal(3L, s.SkippedCounts["same-position"]);
    }

    [Fact]
    public void PositionProgressDto_HasExactlyTheThreeContractKeys_NoNameNorSecret()
    {
        var names = typeof(DiagnosticsPositionProgressDto).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(new[] { "LockBusy", "Propagated", "Throttled" }, names);
    }

    [Fact]
    public void Counters_StartAtZero_AndCountEachKindIndependently()
    {
        var c = new PositionProgressCounters();
        Assert.Equal((0L, 0L, 0L), c.Snapshot());

        c.IncrementPropagated(); c.IncrementPropagated();
        c.IncrementThrottled();
        c.IncrementLockBusy(); c.IncrementLockBusy(); c.IncrementLockBusy();

        Assert.Equal((2L, 1L, 3L), c.Snapshot());
    }

    [Fact]
    public void Counters_SnapshotIsAValue_NotALiveView()
    {
        var c = new PositionProgressCounters();
        var before = c.Snapshot();
        c.IncrementPropagated();

        Assert.Equal((0L, 0L, 0L), before);
        Assert.Equal(1L, c.Snapshot().Propagated);
    }

    [Fact]
    public void Counters_AreThreadSafe_NoLostIncrement()
    {
        var c = new PositionProgressCounters();

        Parallel.For(0, 4000, n =>
        {
            switch (n % 3)
            {
                case 0: c.IncrementPropagated(); break;
                case 1: c.IncrementThrottled(); break;
                default: c.IncrementLockBusy(); break;
            }
        });

        var (p, t, l) = c.Snapshot();
        Assert.Equal(4000, p + t + l);
        Assert.Equal(1334L, p); // 0, 3, 6, … : 1334 valeurs sur 4000
        Assert.Equal(1333L, t);
        Assert.Equal(1333L, l);
    }
}
