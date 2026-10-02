using EmbySharedPlaylist.Core;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// F2-b (v1.2.2, #59, R4a) — retrait « sûr » d'une entrée de playlist. Les identifiants d'entrée d'Emby (ListItemEntryId) sont
/// renumérotés par son worker de rafraîchissement : un identifiant lu puis supprimé plus tard peut désigner un AUTRE média, qui
/// disparaît à la place du média lu (preuve QUALIF : <c>Removal entries=2</c> + <c>Skipped already-removed</c>).
/// <see cref="EntryRemovalGuard"/> relit, supprime, relit ; si un autre média a perdu une entrée, il le ré-ajoute et journalise
/// <c>Error wrong-entry</c>. Les tests simulent le rafraîchissement d'Emby par une liste d'entrées renumérotable (aucun type Emby).
/// </summary>
public class EntryRemovalGuardSpecTests
{
    private const long A = 1, B = 2, C = 3, D = 4;

    /// <summary>Playlist simulée : (ItemId, EntryId) ; EntryId unique, renumérotable.</summary>
    private sealed class Sim
    {
        private long _next = 100;
        public readonly List<(long ItemId, long EntryId)> Entries = new();
        public readonly List<string> Calls = new();
        public readonly List<long> ReAdded = new();
        public readonly List<string> Logged = new();
        /// <summary>Appelé au moment de la 1re lecture, APRÈS qu'elle a été renvoyée : simule le worker qui renumérote juste après.</summary>
        public Action? AfterFirstRead;
        /// <summary>Lectures successives scriptées (si non vide, remplace la lecture réelle, dans l'ordre ; la dernière se répète).</summary>
        public readonly Queue<IReadOnlyList<(long, long)>> ScriptedReads = new();
        private int _reads;

        public Sim(params long[] items) { foreach (var i in items) Entries.Add((i, _next++)); }

        public int Count(long item) => Entries.Count(e => e.ItemId == item);

        /// <summary>Le rafraîchissement d'Emby réattribue TOUS les identifiants, décalés (les mêmes valeurs servent à d'autres médias).</summary>
        public void RenumberShifted()
        {
            var ids = Entries.Select(e => e.EntryId).OrderBy(x => x).ToList();
            for (var i = 0; i < Entries.Count; i++) Entries[i] = (Entries[i].ItemId, ids[(i + 1) % ids.Count]);
        }

        public EntryRemovalGuard Guard(bool throwOnWait = false, bool throwOnReAdd = false, Action? pause = null) =>
            new(
                read: () =>
                {
                    Calls.Add("read");
                    var first = _reads++ == 0;
                    IReadOnlyList<(long ItemId, long EntryId)> snapshot = ScriptedReads.Count > 0
                        ? (ScriptedReads.Count > 1 ? ScriptedReads.Dequeue() : ScriptedReads.Peek()).Select(x => (x.Item1, x.Item2)).ToList()
                        : Entries.ToList();
                    if (first) AfterFirstRead?.Invoke();
                    return snapshot;
                },
                removeByEntryId: id =>
                {
                    Calls.Add($"remove:{id}");
                    var idx = Entries.FindIndex(e => e.EntryId == id);
                    if (idx >= 0) Entries.RemoveAt(idx);
                },
                reAdd: item =>
                {
                    Calls.Add($"readd:{item}");
                    if (throwOnReAdd) throw new InvalidOperationException("AddToPlaylist KO");
                    ReAdded.Add(item); Entries.Add((item, _next++));
                },
                waitRefreshIdle: () => { Calls.Add("wait"); if (throwOnWait) throw new TimeoutException(); },
                logWrongEntry: d => Logged.Add(d),
                pause: pause);

        /// <summary>Boucle du moteur (ReadRemovalEngine) : une entrée à la fois, résolue à l'instant, jusqu'à ce que la cible n'ait plus d'entrée.</summary>
        public int RemoveAll(EntryRemovalGuard g, long target, int max = 50)
        {
            var n = 0;
            while (n < max && g.RemoveOne(target)) n++;
            return n;
        }
    }

    // ---- nominal ----------------------------------------------------------------------------------------------------

    [Fact]
    public void NoRefreshInBetween_RemovesOnlyTheTarget_NoCompensation()
    {
        var sim = new Sim(A, B, C);
        Assert.True(sim.Guard().RemoveOne(B));
        Assert.Equal(new long[] { A, C }, sim.Entries.Select(e => e.ItemId).ToArray());
        Assert.Empty(sim.ReAdded);
        Assert.Empty(sim.Logged);
    }

    [Fact]
    public void Sequence_WaitsForRefreshIdle_ThenReads_ThenRemoves_ThenReReads()
    {
        var sim = new Sim(A, B);
        sim.Guard().RemoveOne(A);
        Assert.Equal("wait", sim.Calls[0]);                 // file de rafraîchissement vidée AVANT la lecture
        Assert.Equal("read", sim.Calls[1]);
        Assert.StartsWith("remove:", sim.Calls[2]);
        Assert.Equal("read", sim.Calls[3]);                 // relecture de vérification
    }

    [Fact]
    public void TargetWithoutAnyEntry_ReturnsFalse_AndRemovesNothing()
    {
        var sim = new Sim(A, C);
        Assert.False(sim.Guard().RemoveOne(B));
        Assert.Equal(2, sim.Entries.Count);
        Assert.DoesNotContain(sim.Calls, c => c.StartsWith("remove:"));
    }

    [Fact]
    public void EntryWithoutEntryId_IsNotResolved_ReturnsFalse()
    {
        var sim = new Sim();
        sim.Entries.Add((B, 0));                           // Emby ne fournit aucun identifiant d'entrée
        Assert.False(sim.Guard().RemoveOne(B));
        Assert.DoesNotContain(sim.Calls, c => c.StartsWith("remove:"));
    }

    // ---- doublons (I5) ----------------------------------------------------------------------------------------------

    [Fact]
    public void Duplicates_AreAllRemovedByTheLoop_OthersUntouched_NoCompensation()
    {
        var sim = new Sim(A, B, A, C, A);
        var removed = sim.RemoveAll(sim.Guard(), A);
        Assert.Equal(3, removed);                           // « Removal entries=3 »
        Assert.Equal(0, sim.Count(A));
        Assert.Equal(1, sim.Count(B));
        Assert.Equal(1, sim.Count(C));
        Assert.Empty(sim.ReAdded);
        Assert.Empty(sim.Logged);
    }

    // ---- renumérotation entre lecture et écriture (défaut v1.2.1) ----------------------------------------------------

    [Fact]
    public void StaleEntryId_RemovingANeighbour_IsCompensatedByReAdd_AndLoggedAsWrongEntry()
    {
        // Lecture : B porte l'entrée 101. Juste après, le worker renumérote (décalage) : 101 désigne désormais un AUTRE média.
        var sim = new Sim(A, B, C, D);
        sim.AfterFirstRead = sim.RenumberShifted;
        var g = sim.Guard();

        Assert.True(g.RemoveOne(B));                        // une entrée a été supprimée… mais pas forcément celle de B

        var wrong = sim.Logged.Single();
        Assert.StartsWith("wrong-entry removed=", wrong);
        Assert.EndsWith($"target={B}", wrong);
        var lost = sim.ReAdded.Single();
        Assert.NotEqual(B, lost);
        Assert.Equal(1, sim.Count(lost));                   // le média retiré à tort est de retour, présent UNE fois
    }

    [Fact]
    public void StaleEntryId_ThenTheLoopContinues_EndsWithOnlyTheTargetGone()
    {
        var sim = new Sim(A, B, C, D);
        sim.AfterFirstRead = sim.RenumberShifted;
        var g = sim.Guard();

        sim.RemoveAll(g, B);

        Assert.Equal(0, sim.Count(B));
        Assert.Equal(1, sim.Count(A));
        Assert.Equal(1, sim.Count(C));
        Assert.Equal(1, sim.Count(D));
    }

    [Fact]
    public void WithoutTheGuard_TheSameRenumberingWouldLoseAnotherMedia_RedCheckOfTheSimulation()
    {
        // Contrôle de la simulation (RED CHECK) : une suppression NAÏVE (lecture puis suppression par l'identifiant lu) perd bien un autre média.
        var sim = new Sim(A, B, C, D);
        var stale = sim.Entries.First(e => e.ItemId == B).EntryId;
        sim.RenumberShifted();
        var idx = sim.Entries.FindIndex(e => e.EntryId == stale);
        var victim = sim.Entries[idx].ItemId;
        sim.Entries.RemoveAt(idx);
        Assert.NotEqual(B, victim);                         // la victime n'est pas B : c'est exactement le défaut
        Assert.Equal(1, sim.Count(B));
    }

    [Fact]
    public void TwoDifferentMediaLost_AreEachReAdded_AndEachLogged()
    {
        var sim = new Sim(A, B, C, D);
        sim.ScriptedReads.Enqueue(sim.Entries.Select(e => (e.ItemId, e.EntryId)).ToList());   // avant : A B C D
        sim.ScriptedReads.Enqueue(new List<(long, long)> { (A, 200), (D, 201) });              // après : B (cible, retirée) ET C perdus + …
        var g = sim.Guard();
        // cible = B ; C est perdu en plus ; A et D restent
        Assert.True(g.RemoveOne(B));
        Assert.Contains(C, sim.ReAdded);
        Assert.DoesNotContain(B, sim.ReAdded);              // jamais la cible
        Assert.All(sim.Logged, l => Assert.StartsWith("wrong-entry removed=", l));
    }

    // ---- confirmation (réinsertion non atomique du worker) ------------------------------------------------------------

    [Fact]
    public void TransientLoss_RestoredByTheWorkerOnTheConfirmationRead_IsNotCompensated()
    {
        var sim = new Sim(A, B, C);
        var before = sim.Entries.Select(e => (e.ItemId, e.EntryId)).ToList();
        sim.ScriptedReads.Enqueue(before);                                                // lecture 1 : A B C
        sim.ScriptedReads.Enqueue(new List<(long, long)> { (A, 300) });                   // lecture 2 : B (cible) ET C absents (réinsertion en cours)
        sim.ScriptedReads.Enqueue(new List<(long, long)> { (A, 300), (C, 301) });         // lecture 3 : C revenu (cible B bien retirée)
        var pauses = 0;
        var g = sim.Guard(pause: () => pauses++);

        Assert.True(g.RemoveOne(B));

        Assert.Equal(1, pauses);                            // la pause précède la 2ᵉ lecture de confirmation
        Assert.Empty(sim.ReAdded);                          // pas de ré-ajout à l'aveugle : aucune duplication
        Assert.Empty(sim.Logged);
    }

    [Fact]
    public void EmptyReadAfterRemoval_WhenSeveralEntriesExisted_IsUnreliable_NoReAddAtTheBlind()
    {
        var sim = new Sim(A, B, C);
        sim.ScriptedReads.Enqueue(sim.Entries.Select(e => (e.ItemId, e.EntryId)).ToList());
        sim.ScriptedReads.Enqueue(new List<(long, long)>());                              // lecture vide (rafraîchissement en cours)
        var g = sim.Guard();

        Assert.True(g.RemoveOne(B));

        Assert.Empty(sim.ReAdded);
        Assert.Empty(sim.Logged);
    }

    // ---- robustesse ----------------------------------------------------------------------------------------------------

    [Fact]
    public void WaitForRefreshThrowing_DoesNotPreventTheRemoval()
    {
        var sim = new Sim(A, B);
        Assert.True(sim.Guard(throwOnWait: true).RemoveOne(A));
        Assert.Equal(0, sim.Count(A));
    }

    [Fact]
    public void RemovalFailure_Propagates_AndNothingIsReAdded()
    {
        var sim = new Sim(A, B);
        var g = new EntryRemovalGuard(
            read: () => sim.Entries.ToList(),
            removeByEntryId: _ => throw new TimeoutException("RemoveFromPlaylist > 5 s"),
            reAdd: i => sim.ReAdded.Add(i),
            waitRefreshIdle: () => { },
            logWrongEntry: d => sim.Logged.Add(d));
        Assert.Throws<TimeoutException>(() => g.RemoveOne(A));
        Assert.Empty(sim.ReAdded);
    }

    [Fact]
    public void ReAddFailing_IsSwallowed_AndTheErrorStaysVisibleInTheJournal()
    {
        var sim = new Sim(A, B, C, D);
        sim.AfterFirstRead = sim.RenumberShifted;
        var g = sim.Guard(throwOnReAdd: true);

        var ex = Record.Exception(() => g.RemoveOne(B));

        Assert.Null(ex);                                    // jamais d'exception : le retrait a eu lieu
        Assert.NotEmpty(sim.Logged);                        // la perte reste journalisée (Error wrong-entry)
    }
}
