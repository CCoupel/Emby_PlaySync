using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Reconciliation;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// Tests de SPÉCIFICATION d'<see cref="AutoSharingService"/> (#26), écrits par scénario de déploiement (une suite
/// d'actions dans l'ordre où un vrai administrateur les vivrait), en complément boîte blanche
/// d'<see cref="AutoSharingServiceDevTests"/> (déjà très complet : interrupteur, no-op déjà actif, jamais de
/// révocation, D-e, isolation des erreurs par utilisateur, échec de la liste, annulation). Même esprit que
/// <see cref="PlaybackSyncTrackerSpecTests"/>/<see cref="PlayedTransitionTrackerSpecTests"/>.
/// </summary>
public class AutoSharingServiceSpecTests
{
    private sealed class Rig
    {
        public readonly FakeUserPolicyGateway Policy = new();
        public readonly ListJournal Journal = new();
        public bool AutoEnableSharing = true;
        public readonly AutoSharingService Service;
        public Rig() => Service = new AutoSharingService(Policy, Journal, new FakeClock(), () => AutoEnableSharing);
    }

    [Fact]
    public void FullLifecycle_Deployment_NewAccount_ManualUncheck_SwitchOff_ThenBackOn()
    {
        var r = new Rig();

        // Jour 0 (déploiement) : deux comptes déjà existants, aucun n'a encore la permission -> première passe.
        r.Policy.AddUser("existing1", sharingEnabled: false);
        r.Policy.AddUser("existing2", sharingEnabled: false);
        r.Service.RunPass();
        Assert.True(r.Policy.IsSharingEnabled("existing1"));
        Assert.True(r.Policy.IsSharingEnabled("existing2"));

        // Un nouveau compte créé PENDANT que le plugin tourne : reçoit la permission SANS attendre de passe (D-d).
        r.Policy.AddUser("newcomer", sharingEnabled: false);
        var callsBeforeCreation = r.Policy.AllUserIdsCalls; // 1 après le RunPass du déploiement (ligne 33) : compteur cumulatif
        r.Service.OnUserCreated("newcomer");
        Assert.True(r.Policy.IsSharingEnabled("newcomer"));
        Assert.Equal(callsBeforeCreation, r.Policy.AllUserIdsCalls); // OnUserCreated : jamais de scan de tous les utilisateurs (un seul)

        // L'administrateur décoche manuellement la case pour existing1 (pas via le service).
        r.Policy.ManuallyDisable("existing1");
        Assert.False(r.Policy.IsSharingEnabled("existing1"));

        // Passe suivante : D-e, réactivé — succès attendu, pas une anomalie.
        r.Service.RunPass();
        Assert.True(r.Policy.IsSharingEnabled("existing1"));

        // L'administrateur éteint maintenant l'interrupteur global, ET décoche existing2 pendant la coupure.
        r.AutoEnableSharing = false;
        r.Policy.ManuallyDisable("existing2");
        r.Service.RunPass();
        Assert.False(r.Policy.IsSharingEnabled("existing2")); // interrupteur éteint : pas reposé...
        Assert.True(r.Policy.IsSharingEnabled("existing1"));  // ...et rien de déjà accordé n'est révoqué non plus

        // Rallumé : le décochage laissé pendant la coupure est reposé dès la passe suivante.
        r.AutoEnableSharing = true;
        r.Service.RunPass();
        Assert.True(r.Policy.IsSharingEnabled("existing2"));
    }

    [Fact]
    public void OnUserCreated_ForAnUnknownUser_NeverThrows_NeverJournals()
    {
        // Cas non couvert par AutoSharingServiceDevTests : EnableSharingIfNeeded renvoie faux pour un utilisateur
        // inconnu (contrat IUserPolicyGateway) — indistinct d'un « déjà actif » du point de vue du service, mais vaut
        // la peine d'être vérifié explicitement à l'appel (l'utilisateur a pu disparaître entre l'événement et le
        // traitement, comme pour les autres ports).
        var r = new Rig();
        var ex = Record.Exception(() => r.Service.OnUserCreated("ghost"));
        Assert.Null(ex);
        Assert.Empty(r.Policy.Enabled);
        Assert.Empty(r.Journal.Of("PermissionPosed"));
    }

    [Fact]
    public void RunPass_WithAnUnknownUserMixedIn_CountsItAsAlreadyEnabled_NeverCrashes()
    {
        // FakeUserPolicyGateway.AllUserIds() ne renvoie que des utilisateurs connus, donc ce cas ne se produit pas
        // en pratique via RunPass — mais AutoSharingService ne fait aucune hypothèse sur la source de la liste
        // (adaptateur réel : IUserManager.Users, cf. IUserPolicyGateway) ; vérifié ici par un faux dédié minimal.
        var policy = new UnknownAwarePolicy();
        var journal = new ListJournal();
        var service = new AutoSharingService(policy, journal, new FakeClock(), () => true);

        var result = service.RunPass();

        Assert.Equal(1, result.Users);
        Assert.Equal(0, result.Enabled);
        Assert.Equal(1, result.AlreadyEnabled); // utilisateur inconnu compté comme « rien à faire », jamais une erreur
        Assert.Empty(journal.Of("Error"));
        Assert.Empty(journal.Of("PermissionPosed"));
    }

    private sealed class UnknownAwarePolicy : IUserPolicyGateway
    {
        public IReadOnlyList<string> AllUserIds() => new[] { "ghost" };   // listé, mais jamais réellement connu
        public bool? IsSharingEnabled(string userId) => null;
        public bool EnableSharingIfNeeded(string userId) => false;        // utilisateur inconnu : jamais d'écriture
    }
}
