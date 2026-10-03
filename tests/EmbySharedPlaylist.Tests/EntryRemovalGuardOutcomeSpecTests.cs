using EmbySharedPlaylist.Core;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// F3 (v1.2.2.1, #59) — <see cref="EntryRemovalGuard.Remove"/> rend une issue : <c>Removed</c> (le compte de la cible a BAISSÉ,
/// <c>TargetRemaining</c> = entrées restantes), <c>NoEffect</c> (identifiant d'entrée périmé : Emby a réécrit sans rien retirer pour la cible),
/// <c>NotFound</c>. Avant F3, une suppression sans effet était comptée comme un retrait (<c>Removal entries=2</c> pour un média présent une fois).
/// </summary>
public class EntryRemovalGuardOutcomeSpecTests
{
    private const long A = 1, B = 2, C = 3;

    /// <summary>Playlist simulée ; <c>staleOnce</c> : le 1er identifiant supprimé ne désigne AUCUNE ligne (renumérotation du worker d'Emby).</summary>
    private static EntryRemovalGuard Guard(List<(long ItemId, long EntryId)> entries, bool staleOnce = false, List<string>? logged = null, List<long>? reAdded = null)
    {
        var stale = staleOnce;
        return new EntryRemovalGuard(
            read: () => entries.ToList(),
            removeByEntryId: id =>
            {
                if (stale) { stale = false; return; }                       // id périmé : aucune ligne supprimée
                var i = entries.FindIndex(e => e.EntryId == id);
                if (i >= 0) entries.RemoveAt(i);
            },
            reAdd: (item, _) => { reAdded?.Add(item); entries.Add((item, 900 + entries.Count)); },
            waitRefreshIdle: () => true,
            logWrongEntry: d => logged?.Add(d));
    }

    private static List<(long ItemId, long EntryId)> Playlist(params long[] items) => items.Select((it, i) => (ItemId: it, EntryId: (long)(100 + i))).ToList();

    [Fact]
    public void NormalCase_IsRemoved_WithNoEntryLeftForTheTarget()
    {
        var entries = Playlist(A, B, C);
        var r = Guard(entries).Remove(B);
        Assert.Equal(RemoveOutcome.Removed, r.Outcome);
        Assert.Equal(0, r.TargetRemaining);
        Assert.False(r.OtherLost);
    }

    [Fact]
    public void DuplicatedTarget_IsRemoved_WithOneEntryRemaining()
    {
        var entries = Playlist(A, B, B);
        var r = Guard(entries).Remove(B);
        Assert.Equal(RemoveOutcome.Removed, r.Outcome);
        Assert.Equal(1, r.TargetRemaining);          // la boucle appelante rappellera, puis s'arrêtera à 0
    }

    [Fact]
    public void StaleEntryId_PointingToNoRow_IsNoEffect_NotARemoval()
    {
        var entries = Playlist(A, B, C);
        var logged = new List<string>();
        var r = Guard(entries, staleOnce: true, logged).Remove(B);

        Assert.Equal(RemoveOutcome.NoEffect, r.Outcome);
        Assert.Equal(1, r.TargetRemaining);          // la cible est toujours là
        Assert.False(r.OtherLost);
        Assert.Equal(3, entries.Count);
        Assert.Empty(logged);                        // aucun autre média touché : pas de wrong-entry
    }

    [Fact]
    public void AfterANoEffect_TheNextAttemptRemoves_AsRemovedWithZeroRemaining()
    {
        var entries = Playlist(A, B, C);
        var g = Guard(entries, staleOnce: true);
        Assert.Equal(RemoveOutcome.NoEffect, g.Remove(B).Outcome);
        var second = g.Remove(B);
        Assert.Equal(RemoveOutcome.Removed, second.Outcome);
        Assert.Equal(0, second.TargetRemaining);
    }

    [Fact]
    public void TargetWithoutAnyEntry_IsNotFound()
    {
        var entries = Playlist(A, C);
        Assert.Equal(RemoveOutcome.NotFound, Guard(entries).Remove(B).Outcome);
    }

    [Fact]
    public void WrongMediaRemoved_TargetStillThere_IsNoEffectWithOtherLost_AndCompensated()
    {
        // L'identifiant lu désigne désormais C : C disparaît, B reste. Compensation (ré-ajout de C + wrong-entry), issue NoEffect, OtherLost.
        var entries = Playlist(A, B, C);
        var logged = new List<string>(); var reAdded = new List<long>();
        var removedWrong = false;
        var g = new EntryRemovalGuard(
            read: () => entries.ToList(),
            removeByEntryId: _ =>
            {
                if (removedWrong) return;
                removedWrong = true;
                entries.RemoveAll(e => e.ItemId == C);                       // le worker avait réattribué l'id de B à C
            },
            reAdd: (item, _) => { reAdded.Add(item); entries.Add((item, 777)); },
            waitRefreshIdle: () => true,
            logWrongEntry: d => logged.Add(d));

        var r = g.Remove(B);

        Assert.Equal(RemoveOutcome.NoEffect, r.Outcome);
        Assert.True(r.OtherLost);
        Assert.Equal(new[] { C }, reAdded);
        Assert.Equal($"wrong-entry removed={C} target={B}", Assert.Single(logged));
        Assert.Equal(1, entries.Count(e => e.ItemId == C));                  // C est revenu une fois
    }

    [Fact]
    public void LegacyRemoveOne_ReturnsTrue_ForRemovedAndNoEffect_FalseForNotFound()
    {
        Assert.True(Guard(Playlist(A, B)).RemoveOne(B));
        Assert.True(Guard(Playlist(A, B), staleOnce: true).RemoveOne(B));
        Assert.False(Guard(Playlist(A)).RemoveOne(B));
    }
}
