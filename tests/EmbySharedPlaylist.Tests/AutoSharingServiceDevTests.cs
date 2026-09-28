using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Reconciliation;
using Xunit;

namespace EmbySharedPlaylist.Tests;

public class AutoSharingServiceDevTests
{
    private sealed class Rig
    {
        public readonly FakeUserPolicyGateway Policy = new();
        public readonly ListJournal Journal = new();
        public bool AutoEnableSharing = true;
        public readonly AutoSharingService Service;

        public Rig()
        {
            Service = new AutoSharingService(Policy, Journal, new FakeClock(), () => AutoEnableSharing);
        }
    }

    // ---- Interrupteur (D-c) ------------------------------------------------------------------------------------

    [Fact]
    public void SwitchOff_NeverListsUsers_NeverWrites_NeverJournals()
    {
        var r = new Rig { AutoEnableSharing = false };
        r.Policy.AddUser("u1", sharingEnabled: false);

        var result = r.Service.RunPass();

        Assert.Equal(0, r.Policy.AllUserIdsCalls); // rien tenté du tout (D-c)
        Assert.Empty(r.Policy.Enabled);
        Assert.Empty(r.Journal.Entries);
        Assert.Equal(new PermissionPassResult(0, 0, 0, result.DurationMs), result);
    }

    [Fact]
    public void OnUserCreated_SwitchOff_NoWrite()
    {
        var r = new Rig { AutoEnableSharing = false };
        r.Policy.AddUser("u1", sharingEnabled: false);
        r.Service.OnUserCreated("u1");
        Assert.Empty(r.Policy.Enabled);
        Assert.Empty(r.Journal.Entries);
    }

    [Fact]
    public void SwitchIsReadLiveOnEveryCall_NotFrozenAtConstruction()
    {
        var r = new Rig { AutoEnableSharing = false };
        r.Policy.AddUser("u1", sharingEnabled: false);
        r.Service.RunPass();
        Assert.Empty(r.Policy.Enabled);

        r.AutoEnableSharing = true; // reconfiguré en direct, sans reconstruire le service
        r.Service.RunPass();
        Assert.Equal(new[] { "u1" }, r.Policy.Enabled);
    }

    // ---- Activation, no-op si déjà actif -----------------------------------------------------------------------

    [Fact]
    public void InactiveUser_IsEnabled_AndJournaledIndividually()
    {
        var r = new Rig();
        r.Policy.AddUser("u1", sharingEnabled: false);

        var result = r.Service.RunPass();

        Assert.Equal(new[] { "u1" }, r.Policy.Enabled);
        Assert.Equal(1, result.Users);
        Assert.Equal(1, result.Enabled);
        Assert.Equal(0, result.AlreadyEnabled);
        var posed = r.Journal.Of("PermissionPosed").Single();
        Assert.Equal("u1", posed.UserId);
        Assert.Null(posed.PlaylistId); // au niveau de l'événement, pas d'une playlist
    }

    [Fact]
    public void AlreadyActiveUser_IsNoOp_NoIndividualJournalEntry()
    {
        var r = new Rig();
        r.Policy.AddUser("u1", sharingEnabled: true);

        var result = r.Service.RunPass();

        Assert.Empty(r.Policy.Enabled);
        Assert.Equal(0, result.Enabled);
        Assert.Equal(1, result.AlreadyEnabled);
        Assert.Empty(r.Journal.Of("PermissionPosed"));
    }

    [Fact]
    public void NeverRevokes_AlreadyActiveIsUntouchedEvenIfEnableWouldBeCalledTwice()
    {
        var r = new Rig();
        r.Policy.AddUser("u1", sharingEnabled: false);
        r.Service.RunPass();
        Assert.True(r.Policy.IsSharingEnabled("u1"));

        r.Policy.Enabled.Clear();
        r.Service.RunPass(); // deuxième passe : déjà actif, jamais réécrit
        Assert.Empty(r.Policy.Enabled);
        Assert.True(r.Policy.IsSharingEnabled("u1"));
    }

    // ---- D-e : aucune mémoire d'un décochage manuel, réactivé à la passe suivante --------------------------------

    [Fact]
    public void ManualDisable_ThenNextPass_ReEnables_D_e()
    {
        var r = new Rig();
        r.Policy.AddUser("u1", sharingEnabled: false);
        r.Service.RunPass();
        Assert.True(r.Policy.IsSharingEnabled("u1"));

        r.Policy.ManuallyDisable("u1"); // décochage manuel simulé (admin), pas via le service
        Assert.False(r.Policy.IsSharingEnabled("u1"));

        var result = r.Service.RunPass();
        Assert.True(r.Policy.IsSharingEnabled("u1")); // réactivé (comportement assumé, pas une mémoire de retrait)
        Assert.Equal(1, result.Enabled);
    }

    // ---- Plusieurs utilisateurs, isolation des erreurs ------------------------------------------------------------

    [Fact]
    public void MultipleUsers_MixOfEnabledAndAlreadyActive_CountedSeparately()
    {
        var r = new Rig();
        r.Policy.AddUser("u1", sharingEnabled: false);
        r.Policy.AddUser("u2", sharingEnabled: true);
        r.Policy.AddUser("u3", sharingEnabled: false);

        var result = r.Service.RunPass();

        Assert.Equal(3, result.Users);
        Assert.Equal(2, result.Enabled);
        Assert.Equal(1, result.AlreadyEnabled);
        Assert.Equal(new[] { "u1", "u3" }, r.Policy.Enabled.OrderBy(x => x));
        var pass = r.Journal.Of("PermissionPass").Single();
        Assert.StartsWith("users=3 enabled=2 alreadyEnabled=1 durationMs=", pass.Detail);
        Assert.Null(pass.PlaylistId);
    }

    [Fact]
    public void AFailingUser_IsIsolated_JournaledByTypeOnly_OtherUsersUnaffected()
    {
        var r = new Rig();
        r.Policy.AddUser("u1", sharingEnabled: false);
        r.Policy.AddUser("u2", sharingEnabled: false);
        r.Policy.AddUser("u3", sharingEnabled: false);
        r.Policy.ThrowOnEnableFor = uid => uid == "u2";

        var result = r.Service.RunPass();

        Assert.Equal(new[] { "u1", "u3" }, r.Policy.Enabled.OrderBy(x => x)); // u2 en échec, les autres traités
        Assert.Equal(new[] { "InvalidOperationException" }, r.Journal.Details("Error"));
        Assert.DoesNotContain("secret", string.Join(" ", r.Journal.Entries.Select(e => e.Detail))); // jamais le message brut
        Assert.Equal(2, result.Enabled);
    }

    [Fact]
    public void CannotListUsers_NeverThrows_JournalsErrorOnly()
    {
        var r = new Rig();
        r.Policy.ThrowOnAllUserIds = true;

        var result = Record.Exception(() => r.Service.RunPass());

        Assert.Null(result);
        Assert.Equal(new[] { "InvalidOperationException" }, r.Journal.Details("Error"));
    }

    // ---- OnUserCreated (#26/D-d) ------------------------------------------------------------------------------

    [Fact]
    public void OnUserCreated_InactiveUser_IsEnabledImmediately_WithoutWaitingForAPass()
    {
        var r = new Rig();
        r.Policy.AddUser("u1", sharingEnabled: false);

        r.Service.OnUserCreated("u1");

        Assert.Equal(new[] { "u1" }, r.Policy.Enabled);
        Assert.Single(r.Journal.Of("PermissionPosed"));
        Assert.Equal(0, r.Policy.AllUserIdsCalls); // un seul utilisateur : jamais besoin de lister tout le monde
    }

    [Fact]
    public void OnUserCreated_AlreadyActive_IsNoOp()
    {
        var r = new Rig();
        r.Policy.AddUser("u1", sharingEnabled: true);
        r.Service.OnUserCreated("u1");
        Assert.Empty(r.Policy.Enabled);
        Assert.Empty(r.Journal.Of("PermissionPosed"));
    }

    [Fact]
    public void OnUserCreated_NeverThrows_EvenOnFailure()
    {
        var r = new Rig();
        r.Policy.AddUser("u1", sharingEnabled: false);
        r.Policy.ThrowOnEnableFor = _ => true;
        var ex = Record.Exception(() => r.Service.OnUserCreated("u1"));
        Assert.Null(ex);
        Assert.Equal(new[] { "InvalidOperationException" }, r.Journal.Details("Error"));
    }

    // ---- Annulation --------------------------------------------------------------------------------------------

    [Fact]
    public void Cancellation_PropagatesAndStopsTheLoop()
    {
        var r = new Rig();
        r.Policy.AddUser("u1", sharingEnabled: false);
        r.Policy.AddUser("u2", sharingEnabled: false);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => r.Service.RunPass(cts.Token));
    }
}
