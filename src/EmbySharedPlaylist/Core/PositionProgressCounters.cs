namespace EmbySharedPlaylist.Core;

/// <summary>
/// Compteurs (mémoire) des <c>PlaybackProgress</c> périodiques du flux avancement (v1.2.1, D23) : ils remplacent une entrée
/// de journal par événement (journal borné à 500). <c>Propagated</c> : événements ayant abouti à une propagation ;
/// <c>Throttled</c> : ignorés (moins de 10 s depuis la dernière propagation) ; <c>LockBusy</c> : ignorés faute de verrou en 250 ms.
/// </summary>
public sealed class PositionProgressCounters
{
    private long _propagated;
    private long _throttled;
    private long _lockBusy;

    public void IncrementPropagated() => Interlocked.Increment(ref _propagated);
    public void IncrementThrottled() => Interlocked.Increment(ref _throttled);
    public void IncrementLockBusy() => Interlocked.Increment(ref _lockBusy);

    public (long Propagated, long Throttled, long LockBusy) Snapshot() =>
        (Interlocked.Read(ref _propagated), Interlocked.Read(ref _throttled), Interlocked.Read(ref _lockBusy));
}
