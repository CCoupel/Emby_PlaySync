using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Engine;
using EmbySharedPlaylist.Reconciliation;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// Tests de SPÉCIFICATION de la propagation du lu (#20/#21) à travers la CHAÎNE RÉELLE telle que <c>PlaybackListener</c>
/// l'assemble : UserDataSaved -> <see cref="PlaybackEventProcessor"/> (WriteScope, puis <see cref="PluginWriteTracker"/> :
/// c'est ICI que l'écho d'une écriture de propagation est consommé, avant tout retour au moteur) -> <see cref="PlayedTransitionTracker"/>
/// -> <see cref="ReadRemovalEngine"/>. Ni <c>EngineDevTests</c> (dev-plugin, appelle <c>Engine.Handle</c> seul, jamais le
/// processeur) ni <c>PlaybackEventProcessorDevTests</c> (dev-plugin, <c>Process()</c> avec un délégué <c>_handle</c> simulé,
/// jamais le vrai moteur) n'exercent cette intégration complète — c'est pourtant le point le plus délicat du lot (D-c,
/// absence de transitivité S6a-c) : réutilise <c>FakeGateway</c>/<c>FakeUserDataGateway</c>/<c>ListJournal</c>/<c>FakeClock</c>
/// (dev-plugin, <c>ReconciliationDevTests.cs</c>), sans les modifier.
/// </summary>
public class PropagationChainSpecTests
{
    private sealed class Chain
    {
        public readonly FakeGateway Gateway = new();
        public readonly FakeUserDataGateway UserDataGateway = new();
        public readonly PluginWriteTracker WriteTracker = new();
        public readonly PlayedTransitionTracker Tracker = new();
        public readonly HandlerStats Stats = new();
        public readonly SeenPlaylists Seen = new();
        public readonly PlaylistLocks Locks = new();
        public readonly ListJournal Journal = new();
        public readonly FakeClock Clock = new();
        public readonly DefaultsService Defaults;
        public readonly ReadRemovalEngine Engine;
        public readonly PlaybackEventProcessor Processor;

        public Chain()
        {
            var timeout = TimeSpan.FromMilliseconds(500);
            Defaults = new DefaultsService(Gateway, Seen, Locks, Journal, "AIDE", () => 2, Clock, timeout);
            Engine = new ReadRemovalEngine(Gateway, UserDataGateway, WriteTracker, Defaults, Seen, Locks, Journal, Clock, timeout);
            Processor = new PlaybackEventProcessor(Tracker, WriteTracker, (u, i) => Engine.Handle(u, i), Stats);
        }

        /// <summary>Playlist déjà vue (les défauts ne sont pas l'objet de ces tests), propriétaire = premier membre.</summary>
        public FakeGateway.State Playlist(string id, string[] tags, params string[] members)
        {
            var s = Gateway.Add(id, tags);
            s.Overview = "déjà";
            s.Members = members.ToList();
            Seen.TryMarkSeen(id);
            return s;
        }

        /// <summary>Événement UserDataSaved réel (y compris l'écho d'une écriture du plugin) tel que le reçoit PlaybackListener.</summary>
        public PlaybackEventOutcome Event(string user, string item, string? reason, bool played) =>
            Processor.Process(user, item, reason, played);
    }

    private static string[] Tags(string csv) => csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [Fact]
    public void OrdinaryTransition_IsHandled_RemovesAndPropagates()
    {
        var c = new Chain();
        var s = c.Playlist("p", Tags("remove-si-lu=OUI,propager-lu=OUI"), "o", "m", "r");
        s.Items = new List<string> { "x" };

        var outcome = c.Event("m", "x", "TogglePlayed", true);

        Assert.Equal(PlaybackEventOutcome.Handled, outcome);
        Assert.Empty(s.Items);
        Assert.True(c.UserDataGateway.IsPlayed("o", "x"));
        Assert.True(c.UserDataGateway.IsPlayed("r", "x"));
        Assert.False(c.UserDataGateway.IsPlayed("m", "x"));   // la source n'est jamais « propagée » à elle-même
    }

    [Fact]
    public void NonTransition_NeverReachesTheEngineNorThePropagationPort()
    {
        var c = new Chain();
        var s = c.Playlist("p", Tags("remove-si-lu=OUI,propager-lu=OUI"), "o", "m", "r");
        s.Items = new List<string> { "x" };

        var outcome = c.Event("m", "x", "PlaybackProgress", false);

        Assert.Equal(PlaybackEventOutcome.NoTransition, outcome);
        Assert.Equal(new[] { "x" }, s.Items);
        Assert.Equal(0, c.Gateway.RemoveCalls);
        Assert.Equal(0, c.UserDataGateway.MarkPlayedCalls);
    }

    [Fact]
    public void S6a_EchoOfAPropagatedWrite_IsConsumedByTheProcessor_NeverReachesTheEngineForTheOtherPlaylist()
    {
        // L1 = {o=U1, m=U12} ; L2 = {o=U1, r=U21} ; F1 dans les deux (S6a, docs/chronogrammes.md §4).
        var c = new Chain();
        var l1 = c.Playlist("L1", Tags("remove-si-lu=OUI,propager-lu=OUI"), "o", "m");
        l1.Items = new List<string> { "F1" };
        var l2 = c.Playlist("L2", Tags("remove-si-lu=OUI,propager-lu=OUI"), "o", "r");
        l2.Items = new List<string> { "F1" };

        // U12 (« m ») lit F1 : seule L1 est candidate (ListSharedPlaylistsOfUserContaining("m", "F1")) : retrait + propagation à « o ».
        var outcome = c.Event("m", "F1", "TogglePlayed", true);
        Assert.Equal(PlaybackEventOutcome.Handled, outcome);
        Assert.Empty(l1.Items);
        Assert.True(c.UserDataGateway.IsPlayed("o", "F1"));
        Assert.Equal(new[] { "F1" }, l2.Items);   // L2 intacte à ce stade : elle n'a jamais été candidate pour CETTE transition

        // La propagation vient de poser Played=true pour « o » : Emby réémettrait un UserDataSaved(o, F1, ..., true).
        // Sans anti-écho, cet événement redéclencherait le moteur pour « o », membre de L2 (F1 y est toujours) : la
        // transitivité interdite par R5/S6a. Le WriteTracker a enregistré (o, F1) AVANT l'écriture (contrat IUserDataGateway).
        var echoOutcome = c.Event("o", "F1", "PlaybackFinished", true);

        Assert.Equal(PlaybackEventOutcome.Echo, echoOutcome);   // reconnu comme écho : JAMAIS transmis au moteur
        Assert.Equal(new[] { "F1" }, l2.Items);                  // L2 toujours intacte : pas de transitivité (déjà vérifié ci-dessus)
        // RemoveOneEntry est rappelé une dernière fois après le retrait pour constater qu'il n'y a plus rien à retirer
        // (même mécanisme que les doublons en v0.2.0, I5) : 2 appels pour la SEULE entrée de F1 dans L1, aucun de plus
        // pour L2 (l'assertion pertinente sur l'absence de transitivité est Assert.Empty/Assert.Equal(l2.Items) ci-dessus).
        Assert.Equal(2, c.Gateway.RemoveCalls);
        Assert.Equal(1, c.UserDataGateway.MarkPlayedCalls);       // aucune écriture de plus que celle de « o » sur L1
    }

    [Fact]
    public void S6a_CallingTheEngineDirectlyOnTheEcho_WouldWronglyReachTheOtherPlaylist_ProvingTheGuardIsNecessary()
    {
        // Contre-épreuve du test précédent : si l'écho de « o » atteignait le moteur SANS passer par le Processor/WriteTracker
        // (comme le ferait un code qui aurait oublié la garde D-c), L2 serait traitée à tort. Ce test isole donc la garde
        // comme la cause réelle de l'absence de transitivité, pas une coïncidence du scénario.
        var c = new Chain();
        var l1 = c.Playlist("L1", Tags("remove-si-lu=OUI,propager-lu=OUI"), "o", "m");
        l1.Items = new List<string> { "F1" };
        var l2 = c.Playlist("L2", Tags("remove-si-lu=OUI,propager-lu=OUI"), "o", "r");
        l2.Items = new List<string> { "F1" };

        c.Event("m", "F1", "TogglePlayed", true);
        Assert.True(c.UserDataGateway.IsPlayed("o", "F1"));

        var direct = c.Engine.Handle("o", "F1");   // appel direct : contourne délibérément WriteScope/WriteTracker

        Assert.True(direct.Candidates >= 1);
        Assert.Empty(l2.Items);   // sans la garde du Processor, L2 SERAIT vidée à tort par cet appel direct
    }

    [Fact]
    public void R7_AlreadyPlayedMember_ThroughTheRealChain_GetsNoWrite()
    {
        var c = new Chain();
        var s = c.Playlist("p", Tags("remove-si-lu=NON,propager-lu=OUI"), "o", "m", "r");
        s.Items = new List<string> { "x" };
        c.UserDataGateway.SetPlayed("r", "x", true);   // r a déjà le flag AVANT la transition (R7)

        var outcome = c.Event("m", "x", "TogglePlayed", true);

        Assert.Equal(PlaybackEventOutcome.Handled, outcome);
        Assert.Equal(1, c.UserDataGateway.MarkPlayedCalls);           // seul « o » est écrit
        Assert.DoesNotContain(("r", "x"), c.UserDataGateway.Marked);  // « r » n'est jamais réécrit
        Assert.Contains(("o", "x"), c.UserDataGateway.Marked);
    }

    [Fact]
    public void R8_MemberWithoutAccess_ThroughTheRealChain_NoWriteNoException()
    {
        var c = new Chain();
        var s = c.Playlist("p", Tags("remove-si-lu=NON,propager-lu=OUI"), "o", "m", "r");
        s.Items = new List<string> { "x" };
        c.UserDataGateway.DenyAccess("r", "x");

        var outcome = c.Event("m", "x", "TogglePlayed", true);

        Assert.Equal(PlaybackEventOutcome.Handled, outcome);
        Assert.True(c.UserDataGateway.IsPlayed("o", "x"));
        Assert.False(c.UserDataGateway.IsPlayed("r", "x") == true);   // toujours sans accès, jamais d'écriture tentée
        Assert.DoesNotContain(("r", "x"), c.UserDataGateway.Marked);
    }

    [Fact]
    public void ReplayOfAnAlreadyPlayedMedia_ThroughTheRealChain_PropagatesNothing_Q1()
    {
        var c = new Chain();
        var s = c.Playlist("p", Tags("remove-si-lu=NON,propager-lu=OUI"), "o", "m", "r");
        s.Items = new List<string> { "x" };
        c.UserDataGateway.SetPlayed("m", "x", true);   // « m » a déjà lu (mémoire du tracker encore vide : PlaybackStart l'enregistre)

        c.Event("m", "x", "PlaybackStart", true);                       // mémorise, aucune transition
        var outcome = c.Event("m", "x", "PlaybackFinished", true);       // relecture jusqu'au bout : pas de transition (Q1)

        Assert.Equal(PlaybackEventOutcome.NoTransition, outcome);
        Assert.Equal(0, c.UserDataGateway.MarkPlayedCalls);
    }
}
