using EmbySharedPlaylist.Core;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// F3-1 (v1.2.2.1, revue) — si la PREMIÈRE attente de fin de rafraîchissement expire (ou lève), le worker d'Emby peut être en pleine
/// réinsertion : la relecture post-écriture ne prouve pas que la cible a disparu. Le garde ne conclut alors jamais « plus rien »
/// (<c>TargetRemaining == -1</c>, inconnu) : la boucle du moteur rappelle (attente + relecture fraîche) au lieu de s'arrêter en laissant
/// éventuellement un média lu dans la liste (R4a). Première attente confirmée : comportement F3 inchangé (0).
/// </summary>
public class EntryRemovalGuardFirstWaitSpecTests
{
    private const long A = 1, B = 2, C = 3;

    private static EntryRemovalGuard Guard(List<(long ItemId, long EntryId)> entries, Func<bool> firstWait)
    {
        var waits = 0;
        return new EntryRemovalGuard(
            read: () => entries.ToList(),
            removeByEntryId: id => { var i = entries.FindIndex(e => e.EntryId == id); if (i >= 0) entries.RemoveAt(i); },
            reAdd: (item, _) => entries.Add((item, 900)),
            waitRefreshIdle: () => waits++ == 0 ? firstWait() : true,
            logWrongEntry: _ => { });
    }

    private static List<(long ItemId, long EntryId)> Playlist(params long[] items) => items.Select((it, i) => (ItemId: it, EntryId: (long)(100 + i))).ToList();

    [Fact]
    public void FirstWaitExpired_TargetAppearsGone_RemainingIsUnknown_NotZero()
    {
        var r = Guard(Playlist(A, B, C), () => false).Remove(B);
        Assert.Equal(RemoveOutcome.Removed, r.Outcome);
        Assert.Equal(-1, r.TargetRemaining);                 // jamais 0 : la boucle doit rappeler
    }

    [Fact]
    public void FirstWaitThrowing_IsTreatedLikeExpired_RemainingUnknown()
    {
        var r = Guard(Playlist(A, B, C), () => throw new TimeoutException()).Remove(B);
        Assert.Equal(RemoveOutcome.Removed, r.Outcome);
        Assert.Equal(-1, r.TargetRemaining);
    }

    [Fact]
    public void FirstWaitExpired_WithDuplicates_RemainingStaysTheObservedCount()
    {
        var r = Guard(Playlist(A, B, B), () => false).Remove(B);
        Assert.Equal(RemoveOutcome.Removed, r.Outcome);
        Assert.Equal(1, r.TargetRemaining);                  // il en reste visiblement une : la boucle rappellera de toute façon
    }

    [Fact]
    public void FirstWaitConfirmed_NormalCase_RemainingIsZero_F3Unchanged()
    {
        var r = Guard(Playlist(A, B, C), () => true).Remove(B);
        Assert.Equal(RemoveOutcome.Removed, r.Outcome);
        Assert.Equal(0, r.TargetRemaining);
    }

    [Fact]
    public void FirstWaitExpired_EmptyReadAfterTheWrite_RemainingIsUnknown()
    {
        var entries = Playlist(A, B, C);
        var reads = 0;
        var g = new EntryRemovalGuard(
            read: () => reads++ == 0 ? entries.ToList() : new List<(long ItemId, long EntryId)>(),   // lecture vide (worker en cours)
            removeByEntryId: _ => { },
            reAdd: (_, _) => { },
            waitRefreshIdle: () => false,
            logWrongEntry: _ => { });
        var r = g.Remove(B);
        Assert.Equal(RemoveOutcome.Removed, r.Outcome);
        Assert.Equal(-1, r.TargetRemaining);
    }

    [Fact]
    public void FirstWaitExpired_EngineLoopSemantics_AUnknownRemainingMeansTheCallerCallsAgain()
    {
        // Contrat avec la boucle du moteur : Removed(-1) n'arrête pas la boucle ; seul 0 l'arrête (ReadRemovalEngineF3SpecTests).
        var r = Guard(Playlist(A, B), () => false).Remove(A);
        Assert.NotEqual(0, r.TargetRemaining);
    }
}
