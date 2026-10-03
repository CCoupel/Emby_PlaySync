using EmbySharedPlaylist.Core;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// F1 — composition des scopes (réserve de revue M1, v1.2.2). La chaîne de jetons rend un scope EXTERNE contaminant : un worker qui
/// capture le contexte dans un scope interne voit Active == true tant que l'externe est vivant, même si l'interne est révoqué.
/// Conséquence de conception : le code appelant ne doit PAS envelopper dans un scope externe une écriture qui lance un worker
/// (ReadRemovalEngine ne doit plus entourer RemoveOneEntry d'un WriteScope : seule la passerelle ouvre le scope, le plus court possible).
/// </summary>
public class WriteScopeCompositionSpecTests
{
    [Fact]
    public async Task OuterScopeStillAlive_WorkerCapturedInTheInnerScope_StillSeesActive_DocumentsTheContamination()
    {
        var innerLeft = new ManualResetEventSlim();
        Task<bool> worker;
        using (WriteScope.Enter())                       // externe (à ne PAS poser autour d'une écriture qui lance un worker)
        {
            var inner = WriteScope.Enter();
            worker = Task.Run(() => { innerLeft.Wait(TimeSpan.FromSeconds(10)); return WriteScope.Active; });
            inner.Dispose();
            innerLeft.Set();
            Assert.True(await worker);                   // l'externe, encore vivant, est dans la chaîne capturée : contamination
        }
    }

    [Fact]
    public async Task WithoutAnOuterScope_WorkerCapturedInTheInnerScope_IsInactiveOnceTheInnerIsDisposed()
    {
        var innerLeft = new ManualResetEventSlim();
        Task<bool> worker;
        var inner = WriteScope.Enter();                  // cas corrigé : seul scope, ouvert par la passerelle
        worker = Task.Run(() => { innerLeft.Wait(TimeSpan.FromSeconds(10)); return WriteScope.Active; });
        inner.Dispose();
        innerLeft.Set();
        Assert.False(await worker);
        Assert.False(WriteScope.Active);
    }

    [Fact]
    public async Task OuterScopeDisposedAfterTheWorkerStarted_WorkerThatReadsLater_SeesInactive()
    {
        var gate = new ManualResetEventSlim();
        Task<bool> worker;
        var outer = WriteScope.Enter();
        var inner = WriteScope.Enter();
        worker = Task.Run(() => { gate.Wait(TimeSpan.FromSeconds(10)); return WriteScope.Active; });
        inner.Dispose();
        outer.Dispose();                                 // les deux révoqués : plus aucun jeton vivant dans la chaîne capturée
        gate.Set();
        Assert.False(await worker);
    }
}
