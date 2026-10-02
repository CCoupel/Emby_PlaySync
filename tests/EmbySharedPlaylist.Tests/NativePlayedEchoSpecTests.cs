using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Engine;
using EmbySharedPlaylist.Reconciliation;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// Tests de SPÉCIFICATION v1.2.0 (docs/chronogrammes.md S9f, plan §1.4, S6/R5) : si Emby pose LUI-MÊME le « lu » chez un
/// membre à la suite d'une position proche de la fin écrite par le plugin (hypothèse à établir par le spike U14b), ce « lu »
/// est d'ORIGINE PLUGIN : l'écho est consommé (<see cref="PluginWriteTracker"/>, puis <see cref="WriteScope"/> pour un éventuel
/// second événement émis sur le MÊME fil que l'écriture, comme le fait l'adaptateur réel <c>EmbyUserDataGateway</c> qui écrit
/// dans un <c>WriteScope</c>) ; la mémoire de transition passe à « lu » ; NI R4a NI R4b ne sont déclenchés — ni dans la
/// playlist du déclencheur, ni dans les AUTRES listes du membre (S6 : aucune transitivité). Chaîne réelle en mémoire :
/// <see cref="PlaybackPositionEngine"/> -> passerelle simulée qui « réagit » comme Emby -> <see cref="PlaybackEventProcessor"/>
/// -> <see cref="PlayedTransitionTracker"/> -> <see cref="ReadRemovalEngine"/> (mêmes instances partagées que
/// <c>PluginRuntime.Initialize</c>). La réserve (événement séparé, hors fil de l'écriture) n'est PAS simulée : c'est l'objet
/// du spike U14b en QUALIF (tests/integration/22-avancement.sh, scénario S9f).
/// </summary>
public class NativePlayedEchoSpecTests
{
    private const long Min = 600_000_000L;

    /// <summary>Simule <c>EmbyUserDataGateway.SetPosition</c> : écriture DANS un WriteScope, puis dispatch SYNCHRONE (même fil)
    /// de <c>UserDataSaved</c> ; option <see cref="NativePlayedAtNearEnd"/> : Emby pose en plus le « lu » (second événement).</summary>
    private sealed class EmbyLikeGateway : IUserDataGateway
    {
        private readonly FakeUserDataGateway _inner;
        private readonly Func<string, string, string, bool, PlaybackEventOutcome> _dispatch;
        public bool NativePlayedAtNearEnd;
        /// <summary>Vrai : Emby pose le « lu » dans la MÊME écriture, donc l'UNIQUE événement porte déjà played=true (modèle à un
        /// événement) ; faux : deux événements successifs sur le même fil (position, puis lu — modèle à deux événements).</summary>
        public bool NativePlayedInFirstEvent;
        public long NearEndTicks = 95 * Min;
        public readonly List<PlaybackEventOutcome> Outcomes = new();

        public EmbyLikeGateway(FakeUserDataGateway inner, Func<string, string, string, bool, PlaybackEventOutcome> dispatch)
        {
            _inner = inner;
            _dispatch = dispatch;
        }

        public bool HasAccess(string userId, string itemId) => _inner.HasAccess(userId, itemId);
        public bool? IsPlayed(string userId, string itemId) => _inner.IsPlayed(userId, itemId);
        public bool MarkPlayed(string userId, string itemId) => _inner.MarkPlayed(userId, itemId);
        public long? GetPosition(string userId, string itemId) => _inner.GetPosition(userId, itemId);

        public bool SetPosition(string userId, string itemId, long ticks)
        {
            using (WriteScope.Enter())
            {
                var wrote = _inner.SetPosition(userId, itemId, ticks);
                if (!wrote) return false;
                if (NativePlayedAtNearEnd && NativePlayedInFirstEvent && ticks >= NearEndTicks) _inner.SetPlayed(userId, itemId, true);
                Outcomes.Add(_dispatch(userId, itemId, "PlaybackProgress", _inner.IsPlayed(userId, itemId) == true));   // 1er événement : l'écho de la position
                if (NativePlayedAtNearEnd && !NativePlayedInFirstEvent && ticks >= NearEndTicks)
                {
                    _inner.SetPlayed(userId, itemId, true);                                                              // Emby pose le « lu » lui-même
                    Outcomes.Add(_dispatch(userId, itemId, "PlaybackProgress", true));                                   // 2e événement, même fil
                }
                return true;
            }
        }
    }

    private sealed class Rig
    {
        public readonly FakeGateway Gateway = new();
        public readonly FakeUserDataGateway Raw = new();
        public readonly PluginWriteTracker WriteTracker = new();
        public readonly SeenPlaylists Seen = new();
        public readonly PlaylistLocks Locks = new();
        public readonly UserItemLocks UserItemLocks = new();
        public readonly ListJournal Journal = new();
        public readonly PlayedTransitionTracker Tracker = new();
        public readonly EmbyLikeGateway Emby;
        public readonly PlaybackPositionEngine PositionEngine;
        public readonly ReadRemovalEngine ReadEngine;
        public int ReadFlowCalls;

        public Rig()
        {
            var clock = new FakeClock();
            var timeout = TimeSpan.FromSeconds(2);
            var defaults = new DefaultsService(Gateway, Seen, Locks, Journal, "AIDE", () => 2, clock, timeout);
            var processor = new PlaybackEventProcessor(Tracker, WriteTracker, (u, i) => { ReadFlowCalls++; return ReadEngine!.Handle(u, i); }, new HandlerStats());
            Emby = new EmbyLikeGateway(Raw, (u, i, reason, played) => processor.Process(u, i, reason, played));
            ReadEngine = new ReadRemovalEngine(Gateway, Emby, WriteTracker, defaults, Seen, Locks, Journal, clock, timeout, userItemLocks: UserItemLocks);
            PositionEngine = new PlaybackPositionEngine(Gateway, Emby, WriteTracker, defaults, Seen, Locks, Journal, clock, timeout, userItemLocks: UserItemLocks);
            Processor = processor;
        }

        public readonly PlaybackEventProcessor Processor;

        public FakeGateway.State Playlist(string id, string tagsCsv, string[] members, params string[] items)
        {
            var s = Gateway.Add(id, tagsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            s.Overview = "déjà";
            s.Members = members.ToList();
            s.Items = items.ToList();
            Seen.TryMarkSeen(id);
            return s;
        }
    }

    [Fact]
    public void S9f_NoNativePlayed_TheMemberStaysUnplayed_TheEchoIsConsumed_NothingElseHappens()
    {
        // Résultat attendu tant que U14b n'a pas établi le contraire : U1 reste non lu et « Reprendre à 99 min 40 s ».
        var r = new Rig();
        var l1 = r.Playlist("L1", "propager-avancement=OUI,remove-si-lu=OUI,propager-lu=NON", new[] { "u1", "u2" }, "F1");

        var result = r.PositionEngine.Handle("u2", "F1", 99 * Min);

        Assert.Equal(1, result.Propagated);
        Assert.Equal(99 * Min, r.Raw.GetPosition("u1", "F1"));
        Assert.False(r.Raw.IsPlayed("u1", "F1") == true);
        Assert.Equal(new[] { PlaybackEventOutcome.Echo }, r.Emby.Outcomes);        // un écho, consommé
        Assert.Equal(0, r.ReadFlowCalls);
        Assert.Equal(new[] { "F1" }, l1.Items);
        Assert.Equal(0, r.WriteTracker.Count);                                     // 1 écriture <-> 1 écho : rien ne reste en attente
    }

    [Fact]
    public void S9f_NativePlayedOnTheSameThread_IsOfPluginOrigin_TriggersNeitherR4aNorR4b_AnywhereForTheMember()
    {
        // L1 : le déclencheur u2 arrête F1 à 99 min ; u1 reçoit la position, Emby pose lui-même le « lu » chez u1.
        // L2 : u1 est aussi membre (avec « r ») ; retrait + propagation actifs, F1 y figure : si le « lu » natif était pris pour
        // une transition d'origine utilisateur, F1 serait retiré de L2 et le lu propagé à « r » (violation de S6/R5).
        var r = new Rig { };
        r.Emby.NativePlayedAtNearEnd = true;
        var l1 = r.Playlist("L1", "propager-avancement=OUI,remove-si-lu=OUI,propager-lu=OUI", new[] { "u1", "u2" }, "F1");
        var l2 = r.Playlist("L2", "remove-si-lu=OUI,propager-lu=OUI,propager-avancement=NON", new[] { "u1", "r" }, "F1");

        r.PositionEngine.Handle("u2", "F1", 99 * Min);

        Assert.Equal(new[] { PlaybackEventOutcome.Echo, PlaybackEventOutcome.WriteScope }, r.Emby.Outcomes);   // écho consommé, 2e événement ignoré (WriteScope)
        Assert.Equal(0, r.ReadFlowCalls);                                          // le moteur du lu n'est JAMAIS appelé
        Assert.Equal(new[] { "F1" }, l1.Items);                                    // ni retrait dans la playlist du déclencheur…
        Assert.Equal(new[] { "F1" }, l2.Items);                                    // …ni dans les autres listes du membre (S6)
        Assert.Equal(0, r.Raw.MarkPlayedCalls);                                    // et aucun lu propagé par le plugin
        Assert.False(r.Raw.IsPlayed("r", "F1") == true);
        Assert.Empty(r.Journal.Of("Removal"));
        Assert.Empty(r.Journal.Of("Propagation"));
        Assert.True(r.Raw.IsPlayed("u1", "F1"));                                   // le « lu » natif d'Emby est bien là (état posé par l'hôte)
    }

    [Fact]
    public void S9f_SingleEventModel_TheEchoCarriesPlayedTrue_ConsumedNoEngineCall_MemoryUpdated()
    {
        // Modèle à UN événement : Emby pose le « lu » dans la même écriture ; l'unique événement est l'écho de la position (played=true).
        var r = new Rig();
        r.Emby.NativePlayedAtNearEnd = true;
        r.Emby.NativePlayedInFirstEvent = true;
        var l1 = r.Playlist("L1", "propager-avancement=OUI,remove-si-lu=OUI,propager-lu=OUI", new[] { "u1", "u2" }, "F1");
        var l2 = r.Playlist("L2", "remove-si-lu=OUI,propager-lu=OUI", new[] { "u1", "r" }, "F1");

        r.PositionEngine.Handle("u2", "F1", 99 * Min);

        Assert.Equal(new[] { PlaybackEventOutcome.Echo }, r.Emby.Outcomes);
        Assert.Equal(0, r.ReadFlowCalls);
        Assert.Equal(new[] { "F1" }, l1.Items);
        Assert.Equal(new[] { "F1" }, l2.Items);
        Assert.Equal(PlaybackEventOutcome.NoTransition, r.Processor.Process("u1", "F1", "PlaybackFinished", true));   // la mémoire dit « lu » (Q1)
    }

    [Fact]
    public void S9f_AfterAConsumedNativeEcho_TheTransitionMemoryOfTheMemberSaysPlayed_ANextReplayIsNoTransition()
    {
        // Modèle à DEUX événements (position puis lu, même fil) : le second est ignoré par WriteScope ; la spec exige pourtant que la
        // mémoire de transition de U1 passe à « lu » (S9f) — sinon relire F1 jusqu'au bout serait pris pour une transition (retrait
        // parasite). Si ce test est ROUGE : PlaybackEventProcessor doit mettre la mémoire à jour AUSSI pour l'événement ignoré par WriteScope.
        var r = new Rig();
        r.Emby.NativePlayedAtNearEnd = true;
        r.Playlist("L1", "propager-avancement=OUI,remove-si-lu=OUI,propager-lu=OUI", new[] { "u1", "u2" }, "F1");
        r.PositionEngine.Handle("u2", "F1", 99 * Min);

        // u1 relit ensuite F1 jusqu'au bout : la mémoire connaît déjà « lu » (mise à jour par l'écho, même consommé) -> Q1 : aucune transition.
        Assert.Equal(PlaybackEventOutcome.NoTransition, r.Processor.Process("u1", "F1", "PlaybackFinished", true));
        Assert.Equal(0, r.ReadFlowCalls);
    }

    [Fact]
    public void S9f_ARealUserTransitionAfterTheEcho_IsStillHandled_TheGuardDoesNotSwallowRealEvents()
    {
        // Contre-épreuve : l'anti-écho n'est pas un trou noir. Un vrai « lu » d'un autre membre (hors écriture du plugin) déclenche bien le tableau A.
        var r = new Rig();
        r.Emby.NativePlayedAtNearEnd = true;
        var l1 = r.Playlist("L1", "propager-avancement=OUI,remove-si-lu=OUI,propager-lu=OUI", new[] { "u1", "u2", "u3" }, "F1");
        r.PositionEngine.Handle("u2", "F1", 99 * Min);
        Assert.Equal(0, r.ReadFlowCalls);

        var outcome = r.Processor.Process("u3", "F1", "TogglePlayed", true);       // action utilisateur réelle, hors WriteScope

        Assert.Equal(PlaybackEventOutcome.Handled, outcome);
        Assert.Equal(1, r.ReadFlowCalls);
        Assert.Empty(l1.Items);                                                    // retrait (R4a) : les deux familles sont actives
    }

    [Fact]
    public void EachPluginWrite_RegistersExactlyOneEcho_NoDanglingEntryWhenNothingWasWritten()
    {
        // 1 écriture <-> 1 écho attendu : same-position (aucune écriture) ne doit pas laisser d'entrée en attente dans le tracker
        // (elle marquerait à tort « plugin » une écriture utilisateur suivante sur le même couple).
        var r = new Rig();
        r.Playlist("L1", "propager-avancement=OUI", new[] { "u1", "u2" }, "F1");
        r.PositionEngine.Handle("u2", "F1", 10 * Min);                             // écrit u1 : un écho, consommé
        Assert.Equal(0, r.WriteTracker.Count);
        r.PositionEngine.Handle("u2", "F1", 10 * Min);                             // même position : rien n'est écrit
        Assert.Equal(0, r.WriteTracker.Count);
        Assert.Contains("same-position", r.Journal.Details("Skipped"));
    }

    [Fact]
    public void V121_CompletionZeroWrite_ProducesOneConsumedEcho_NoReadFlow_NoRemoval_NothingPending()
    {
        // #58 : la remise à 0 de fin de lecture est une écriture du plugin comme une autre : un écho, consommé ; ni R4a ni R4b.
        var r = new Rig();
        var l1 = r.Playlist("L1", "propager-avancement=OUI,propager-lu=OUI,remove-si-lu=OUI", new[] { "u1", "u2" }, "F1");
        r.Raw.SetPlayed("u1", "F1", true);            // le flux du lu est déjà passé chez u1
        r.Raw.SetPosition("u1", "F1", 95 * Min);      // point de reprise laissé par la propagation périodique

        r.PositionEngine.Handle("u2", "F1", 100 * Min, PositionTrigger.Completion);

        Assert.Equal(0L, r.Raw.GetPosition("u1", "F1"));
        Assert.Equal(new[] { PlaybackEventOutcome.Echo }, r.Emby.Outcomes);
        Assert.Equal(0, r.ReadFlowCalls);
        Assert.Equal(new[] { "F1" }, l1.Items);       // ce moteur ne retire jamais rien
        Assert.Equal(0, r.WriteTracker.Count);
    }
}
