using EmbySharedPlaylist.Core;
using Xunit;

namespace EmbySharedPlaylist.Tests;

public class WriteScopeTests
{
    [Fact]
    public void Active_IsFalseByDefault() => Assert.False(WriteScope.Active);

    [Fact]
    public void Enter_ActivatesTheScopeUntilDisposed()
    {
        using (WriteScope.Enter()) Assert.True(WriteScope.Active);
        Assert.False(WriteScope.Active);
    }

    [Fact]
    public void NestedScopes_RestoreTheOuterStateInOrder()
    {
        var outer = WriteScope.Enter();
        var inner = WriteScope.Enter();
        inner.Dispose();
        Assert.True(WriteScope.Active);
        outer.Dispose();
        Assert.False(WriteScope.Active);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var outer = WriteScope.Enter();
        var inner = WriteScope.Enter();
        inner.Dispose();
        inner.Dispose(); // ne doit pas désactiver le scope externe
        Assert.True(WriteScope.Active);
        outer.Dispose();
        Assert.False(WriteScope.Active);
    }

    [Fact]
    public void Scope_IsRestoredEvenWhenAnExceptionIsThrown()
    {
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using (WriteScope.Enter()) throw new InvalidOperationException();
        }));
        Assert.False(WriteScope.Active);
    }

    [Fact]
    public async Task Scope_FlowsThroughAwaitButNotToUnrelatedWork()
    {
        var unrelatedStarted = new ManualResetEventSlim();
        var unrelatedMayRead = new ManualResetEventSlim();
        var unrelatedSawActive = true;
        var unrelated = Task.Run(() =>
        {
            unrelatedStarted.Set();
            unrelatedMayRead.Wait();
            unrelatedSawActive = WriteScope.Active;
        });
        unrelatedStarted.Wait();

        using (WriteScope.Enter())
        {
            await Task.Yield();
            Assert.True(WriteScope.Active);   // le contexte suit la continuation
            unrelatedMayRead.Set();
            await unrelated;
        }

        Assert.False(unrelatedSawActive);     // une écriture d'un autre contexte n'est pas affectée
        Assert.False(WriteScope.Active);
    }

    [Fact]
    public void AThreadThatDoesNotInheritTheContext_DoesNotSeeTheScope()
    {
        bool otherSawActive = true;
        using (WriteScope.Enter())
        {
            var t = new Thread(() => otherSawActive = WriteScope.Active);
            using (ExecutionContext.SuppressFlow()) t.Start(); // fil sans le contexte d'exécution de l'appelant
            t.Join();
            Assert.True(WriteScope.Active);
        }
        Assert.False(otherSawActive);
    }
}
