using EmbySharedPlaylist.Core;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// Réserve de revue M2 (v1.2.2, F2-b) : si l'attente de fin de rafraîchissement EXPIRE (file toujours active) au moment de confirmer
/// une perte, le garde ne ré-ajoute PAS (le worker d'Emby peut encore réinsérer : risque de doublon) et journalise
/// <c>wrong-entry-unconfirmed removed=&lt;ItemId&gt; target=&lt;ItemId&gt;</c>. Idle confirmé : ré-ajout + <c>wrong-entry</c>, avec
/// skipDuplicates = vrai uniquement si l'ItemId n'avait qu'UNE entrée avant le retrait.
/// </summary>
public class EntryRemovalGuardUnconfirmedSpecTests
{
    private const long A = 1, B = 2, C = 3;

    private sealed class Rig
    {
        public readonly List<(long Lost, bool SkipDuplicates)> ReAdded = new();
        public readonly List<string> Logged = new();
        public int WaitCalls;
        private readonly Queue<IReadOnlyList<(long, long)>> _reads = new();
        public void Reads(params (long, long)[][] sequence) { foreach (var s in sequence) _reads.Enqueue(s); }

        /// <param name="waitResults">résultat de chaque appel de waitRefreshIdle, dans l'ordre (le dernier se répète) ; null = lève</param>
        public EntryRemovalGuard Guard(params bool?[] waitResults) => new(
            read: () => (_reads.Count > 1 ? _reads.Dequeue() : _reads.Peek()).Select(x => (x.Item1, x.Item2)).ToList(),
            removeByEntryId: _ => { },
            reAdd: (lost, skip) => ReAdded.Add((lost, skip)),
            waitRefreshIdle: () =>
            {
                var r = waitResults[Math.Min(WaitCalls++, waitResults.Length - 1)];
                return r ?? throw new TimeoutException("file de rafraîchissement active");
            },
            logWrongEntry: d => Logged.Add(d));
    }

    [Fact]
    public void WaitExpired_OnTheConfirmation_NoReAdd_LogsWrongEntryUnconfirmed()
    {
        var rig = new Rig();
        rig.Reads(new[] { (A, 10L), (B, 11L), (C, 12L) }, new[] { (A, 10L) });   // avant : A B C ; après : B (cible) ET C absents
        var g = rig.Guard(true, false);                                           // 1er wait (avant lecture) OK ; 2e (confirmation) expire

        Assert.True(g.RemoveOne(B));

        Assert.Empty(rig.ReAdded);                                                // jamais de ré-ajout tant que le worker est actif
        var log = Assert.Single(rig.Logged);
        Assert.Equal($"wrong-entry-unconfirmed removed={C} target={B}", log);
    }

    [Fact]
    public void WaitThrowing_OnTheConfirmation_IsTreatedAsUnconfirmed()
    {
        var rig = new Rig();
        rig.Reads(new[] { (A, 10L), (B, 11L), (C, 12L) }, new[] { (A, 10L) });
        var g = rig.Guard(true, null);

        Assert.True(g.RemoveOne(B));

        Assert.Empty(rig.ReAdded);
        Assert.StartsWith("wrong-entry-unconfirmed removed=", Assert.Single(rig.Logged));
    }

    [Fact]
    public void WaitConfirmed_ReAddsTheLostMedia_AsWrongEntry_WithSkipDuplicatesForASingleEntry()
    {
        var rig = new Rig();
        rig.Reads(new[] { (A, 10L), (B, 11L), (C, 12L) }, new[] { (A, 10L) });
        var g = rig.Guard(true, true);

        Assert.True(g.RemoveOne(B));

        var re = Assert.Single(rig.ReAdded);
        Assert.Equal(C, re.Lost);
        Assert.True(re.SkipDuplicates);                                           // C n'avait qu'UNE entrée : ne pas dupliquer si elle est revenue
        Assert.Equal($"wrong-entry removed={C} target={B}", Assert.Single(rig.Logged));
    }

    [Fact]
    public void WaitConfirmed_WithDuplicatedLostMedia_ReAddsWithoutSkipDuplicates()
    {
        var rig = new Rig();
        rig.Reads(new[] { (A, 10L), (B, 11L), (C, 12L), (C, 13L) }, new[] { (A, 10L), (C, 13L) });   // C avait 2 entrées, 1 perdue
        var g = rig.Guard(true, true);

        Assert.True(g.RemoveOne(B));

        var re = Assert.Single(rig.ReAdded);
        Assert.Equal(C, re.Lost);
        Assert.False(re.SkipDuplicates);                                          // doublon légitime : le ré-ajout doit pouvoir ajouter une 2e entrée
    }

    [Fact]
    public void NoLoss_NeverWaitsForConfirmation_NeverLogs()
    {
        var rig = new Rig();
        rig.Reads(new[] { (A, 10L), (B, 11L), (C, 12L) }, new[] { (A, 10L), (C, 12L) });
        var g = rig.Guard(true, false);

        Assert.True(g.RemoveOne(B));

        Assert.Equal(1, rig.WaitCalls);                                           // seule l'attente AVANT la lecture ; pas de confirmation nécessaire
        Assert.Empty(rig.ReAdded);
        Assert.Empty(rig.Logged);
    }
}
