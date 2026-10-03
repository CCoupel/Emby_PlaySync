using EmbySharedPlaylist.Core;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// F1 (v1.2.2, #59) — le WriteScope ne doit PAS fuir dans un worker de l'hôte qui a capturé l'ExecutionContext du plugin
/// (Task.Run dans ProviderManager.StartProcessingRefreshQueue, lancé pendant RemoveFromPlaylist). Après Dispose du scope, une
/// copie capturée du contexte doit voir Active == false ; pendant le scope, true ; une continuation await dans le scope reste
/// active ; la révocation d'un scope enfant n'éteint pas le parent. Tests additionnels (WriteScopeTests.cs reste immuable).
/// RED CHECK : avec l'implémentation AsyncLocal&lt;int&gt;, les tests « AfterDispose » échouent (la copie garde Depth = 1).
/// </summary>
public class WriteScopeLeakSpecTests
{
    [Fact]
    public async Task TaskRunStartedInsideTheScope_SeesItActiveWhileTheScopeLives()
    {
        using (WriteScope.Enter())
        {
            var seen = await Task.Run(() => WriteScope.Active);
            Assert.True(seen);
        }
    }

    [Fact]
    public async Task TaskRunStartedInsideTheScope_SeesItInactiveAfterDispose()
    {
        // Simule le worker d'Emby : démarré dans le scope (capture le contexte), il lit Active APRÈS la sortie du scope.
        var scopeLeft = new ManualResetEventSlim();
        Task<bool> worker;
        using (WriteScope.Enter())
        {
            worker = Task.Run(() =>
            {
                scopeLeft.Wait(TimeSpan.FromSeconds(10));
                return WriteScope.Active;
            });
        }
        scopeLeft.Set();
        Assert.False(await worker);
        Assert.False(WriteScope.Active);
    }

    [Fact]
    public async Task LongLivedWorkerLoop_StopsSeeingTheScopeOnceItIsRevoked()
    {
        // Le worker de rafraîchissement traite toute la file : chaque itération (ItemUpdated) doit être « hors scope » après Dispose.
        var scopeLeft = new ManualResetEventSlim();
        Task<bool[]> worker;
        using (WriteScope.Enter())
        {
            worker = Task.Run(() =>
            {
                scopeLeft.Wait(TimeSpan.FromSeconds(10));
                var seen = new bool[3];
                for (var i = 0; i < seen.Length; i++) { seen[i] = WriteScope.Active; Thread.Sleep(5); }
                return seen;
            });
        }
        scopeLeft.Set();
        Assert.All(await worker, active => Assert.False(active));
    }

    [Fact]
    public void NestedScopes_RevokingTheChildAloneKeepsTheParentActive()
    {
        using var outer = WriteScope.Enter();
        var inner = WriteScope.Enter();
        Assert.True(WriteScope.Active);
        inner.Dispose();
        Assert.True(WriteScope.Active);   // le parent n'est pas révoqué
    }

    [Fact]
    public async Task ChildCapturedByATask_IsInactiveAfterTheChildIsDisposed_ButParentStaysActiveForItsOwnWork()
    {
        using var outer = WriteScope.Enter();
        var childLeft = new ManualResetEventSlim();
        Task<bool> childWorker;
        var inner = WriteScope.Enter();
        childWorker = Task.Run(() =>
        {
            childLeft.Wait(TimeSpan.FromSeconds(10));
            // Le contexte capturé contient le jeton de l'enfant (révoqué) chaîné au parent (toujours actif) : Active dépend
            // de l'existence d'UN jeton non révoqué dans la chaîne — le parent l'est encore, donc true.
            return WriteScope.Active;
        });
        inner.Dispose();
        childLeft.Set();
        Assert.True(await childWorker);   // parent encore vivant
        Assert.True(WriteScope.Active);
    }

    [Fact]
    public async Task AwaitedContinuationInsideTheScope_StaysActive()
    {
        using (WriteScope.Enter())
        {
            await Task.Delay(10);
            Assert.True(WriteScope.Active);
            await Task.Yield();
            Assert.True(WriteScope.Active);
        }
        Assert.False(WriteScope.Active);
    }

    [Fact]
    public async Task AwaitedTasksStartedInsideTheScope_AreActiveForTheirWholeAwaitedLifetime()
    {
        using (WriteScope.Enter())
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                await Task.Delay(5);
                return WriteScope.Active;
            })));
            Assert.All(results, r => Assert.True(r));
        }
    }

    [Fact]
    public async Task ASecondScopeEnteredAfterTheFirstWasRevoked_IsActiveAgain_AndDoesNotReviveTheOldCopy()
    {
        var gate = new ManualResetEventSlim();
        Task<bool> stale;
        using (WriteScope.Enter())
        {
            stale = Task.Run(() => { gate.Wait(TimeSpan.FromSeconds(10)); return WriteScope.Active; });
        }
        using (WriteScope.Enter())
        {
            Assert.True(WriteScope.Active);     // nouveau scope : actif
            gate.Set();
            Assert.False(await stale);          // la copie capturée du PREMIER scope reste révoquée
        }
    }
}
