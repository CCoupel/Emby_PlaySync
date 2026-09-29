using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Engine;
using EmbySharedPlaylist.Reconciliation;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// Tests de SPÉCIFICATION v1.2.0 (#56, #57, D21) : les DEUX tableaux indépendants de docs/chronogrammes.md « Matrices des
/// étiquettes » — tableau A (un membre passe un média à lu : <see cref="ReadRemovalEngine"/>, flux <c>UserDataSaved</c>) et
/// tableau B (pause/arrêt, position ≥ 30 s : <see cref="PlaybackPositionEngine"/>, flux <c>ISessionManager</c>) — plus
/// l'indépendance des flux (S9e), la relecture d'un média déjà lu (S9d, #57) et la position de fin (S9f). Écrits depuis la
/// spécification et les contrats, avant le code final ; réutilisent les faux partagés de <c>ReconciliationDevTests.cs</c>.
/// « Non active » = NON, absente ou OUI+NON. Aucun tableau ne lit l'étiquette de l'autre ni l'état lu pour décider.
/// </summary>
public class TableauxABSpecTests
{
    private const long Min = 600_000_000L;   // 1 minute en ticks

    private sealed class Rig
    {
        public readonly FakeGateway Gateway = new();
        public readonly FakeUserDataGateway UserData = new();
        public readonly PluginWriteTracker WriteTracker = new();
        public readonly SeenPlaylists Seen = new();
        public readonly PlaylistLocks Locks = new();
        public readonly UserItemLocks UserItemLocks = new();
        public readonly ListJournal Journal = new();
        public readonly FakeClock Clock = new();
        public readonly ReadRemovalEngine ReadEngine;
        public readonly PlaybackPositionEngine PositionEngine;

        public Rig()
        {
            var timeout = TimeSpan.FromSeconds(2);
            var defaults = new DefaultsService(Gateway, Seen, Locks, Journal, "AIDE", () => 2, Clock, timeout);
            ReadEngine = new ReadRemovalEngine(Gateway, UserData, WriteTracker, defaults, Seen, Locks, Journal, Clock, timeout, userItemLocks: UserItemLocks);
            PositionEngine = new PlaybackPositionEngine(Gateway, UserData, WriteTracker, defaults, Seen, Locks, Journal, Clock, timeout, userItemLocks: UserItemLocks);
        }

        /// <summary>Playlist déjà vue, membres « o » (propriétaire), « u » (déclencheur) et « v ».</summary>
        public FakeGateway.State Playlist(string id, string tagsCsv, params string[] items)
        {
            var tags = tagsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var s = Gateway.Add(id, tags);
            s.Overview = "déjà";
            s.Items = items.ToList();
            s.Members = new List<string> { "o", "u", "v" };
            Seen.TryMarkSeen(id);
            return s;
        }
    }

    // ================================================================= Tableau A (R4a/R4b)

    [Theory]
    // propager-lu non active => RIEN, quelle que soit remove-si-lu (S3b : changement BREAKING v1.2.0)
    [InlineData("", false, false)]
    [InlineData("remove-si-lu=NON", false, false)]
    [InlineData("remove-si-lu=OUI", false, false)]
    [InlineData("propager-lu=NON,remove-si-lu=OUI", false, false)]
    [InlineData("propager-lu=OUI,propager-lu=NON,remove-si-lu=OUI", false, false)]
    [InlineData("propager-lu=NON", false, false)]
    [InlineData("propager-avancement=OUI,remove-si-lu=OUI", false, false)]   // l'avancement n'active pas le lu
    // propager-lu OUI seule
    [InlineData("propager-lu=OUI", false, true)]                              // flag posé, le média RESTE
    [InlineData("propager-lu=OUI,remove-si-lu=NON", false, true)]
    [InlineData("propager-lu=OUI,remove-si-lu=OUI,remove-si-lu=NON", false, true)]   // NON l'emporte pour remove-si-lu
    [InlineData("propager-lu=OUI,propager-avancement=OUI", false, true)]      // indifférent à l'avancement
    // les deux actives
    [InlineData("propager-lu=OUI,remove-si-lu=OUI", true, true)]
    [InlineData("PROPAGER-LU = oui,REMOVE-SI-LU=Oui", true, true)]
    [InlineData("propager-lu=OUI,remove-si-lu=OUI,propager-avancement=NON", true, true)]
    public void TableauA_OnAReadTransition(string tagsCsv, bool expectRemoved, bool expectFlagPropagated)
    {
        var r = new Rig();
        var s = r.Playlist("p", tagsCsv, "m1", "m2");

        r.ReadEngine.Handle("u", "m1");

        Assert.Equal(expectRemoved ? new[] { "m2" } : new[] { "m1", "m2" }, s.Items);
        Assert.Equal(expectFlagPropagated, r.UserData.IsPlayed("v", "m1") == true);
        Assert.Equal(expectFlagPropagated, r.UserData.IsPlayed("o", "m1") == true);
        Assert.False(r.UserData.Marked.Contains(("u", "m1")));                 // le déclencheur n'est jamais réécrit (R9)
        Assert.Equal(0, r.UserData.SetPositionCalls);                          // R4b n'écrit JAMAIS de position (D21)
        Assert.Empty(r.Journal.Of("PositionPropagation"));
    }

    [Fact]
    public void S3b_RemoveSiLuOuiWithPropagerLuNon_DoesNothing_JournalsSkippedInactive_NoTagChange()
    {
        var r = new Rig();
        var s = r.Playlist("p", "remove-si-lu=OUI,propager-lu=NON,propager-avancement=NON", "F1");
        var tagsBefore = s.Tags.ToList();

        var result = r.ReadEngine.Handle("u", "F1");

        Assert.Equal(new[] { "F1" }, s.Items);
        Assert.Equal(0, r.Gateway.RemoveCalls);
        Assert.Equal(0, r.UserData.MarkPlayedCalls);
        Assert.Empty(r.Journal.Of("Removal"));
        Assert.Empty(r.Journal.Of("Propagation"));
        Assert.Contains("inactive", r.Journal.Details("Skipped"));
        Assert.Equal(tagsBefore, s.Tags);                                     // aucune étiquette modifiée par le plugin : l'étiquette est inerte
        Assert.Equal(0, result.EntriesRemoved);
        Assert.Equal(0, r.Gateway.ApplyCalls);
    }

    [Fact]
    public void S3c_PropagerLuAlone_PropagatesTheFlagOnly_TheMediaStaysInTheList_NoPosition()
    {
        var r = new Rig();
        var s = r.Playlist("p", "remove-si-lu=NON,propager-lu=OUI", "F1");
        r.ReadEngine.Handle("u", "F1");
        Assert.Equal(new[] { "F1" }, s.Items);
        Assert.True(r.UserData.IsPlayed("o", "F1"));
        Assert.True(r.UserData.IsPlayed("v", "F1"));
        Assert.Equal(0, r.UserData.SetPositionCalls);
        Assert.Single(r.Journal.Of("Propagation"));
        Assert.Empty(r.Journal.Of("Removal"));
    }

    [Fact]
    public void TableauA_MarkerSeen_IsJournaledForBothFamilies_v120()
    {
        var r = new Rig();
        r.Playlist("p", "remove-si-lu=OUI,propager-lu=NON", "F1");
        r.ReadEngine.Handle("u", "F1");
        var seen = r.Journal.Details("MarkerSeen").ToList();
        Assert.Contains("family=remove-si-lu state=Oui", seen);
        Assert.Contains("family=propager-lu state=Non", seen);
    }

    // ================================================================= Tableau B (R10)

    [Theory]
    [InlineData("", false)]
    [InlineData("propager-avancement=NON", false)]
    [InlineData("propager-avancement=OUI,propager-avancement=NON", false)]
    [InlineData("propager-lu=OUI", false)]                                    // BREAKING v1.2.0 : propager-lu ne couvre plus l'avancement
    [InlineData("remove-si-lu=OUI,propager-lu=OUI", false)]
    [InlineData("remove-si-lu=OUI,propager-lu=OUI,propager-avancement=NON", false)]
    [InlineData("propager-avancement=OUI", true)]
    [InlineData("PROPAGER-AVANCEMENT = oui", true)]
    [InlineData("propager-lu=NON,remove-si-lu=NON,propager-avancement=OUI", true)]   // indépendant des deux autres familles
    [InlineData("propager-lu=OUI,remove-si-lu=OUI,propager-avancement=OUI", true)]
    public void TableauB_OnPauseOrStop(string tagsCsv, bool expectPropagated)
    {
        var r = new Rig();
        var s = r.Playlist("p", tagsCsv, "m1");

        r.PositionEngine.Handle("u", "m1", 10 * Min);

        Assert.Equal(expectPropagated ? 10 * Min : 0L, r.UserData.GetPosition("v", "m1"));
        Assert.Equal(expectPropagated ? 10 * Min : 0L, r.UserData.GetPosition("o", "m1"));
        Assert.Equal(0, r.UserData.MarkPlayedCalls);                           // aucun flag lu n'est jamais posé par le tableau B
        Assert.Equal(new[] { "m1" }, s.Items);                                 // jamais de retrait
        Assert.Equal(expectPropagated ? 1 : 0, r.Journal.Of("PositionPropagation").Count());
        if (!expectPropagated) Assert.Contains("inactive", r.Journal.Details("Skipped"));
    }

    [Fact]
    public void TableauB_NeverConsultsAnyPlayedState_TriggerNorMembers_AllPlayedOrNot()
    {
        // Aucune garde sur l'état lu (#57) : déclencheur lu, membres lus / non lus / accès refusé (R8 seul cas d'exclusion).
        var r = new Rig();
        r.Playlist("p", "propager-avancement=OUI", "m1").Members = new List<string> { "o", "u", "v", "w" };
        r.UserData.SetPlayed("u", "m1", true);   // déclencheur déjà lu
        r.UserData.SetPlayed("o", "m1", true);   // membre déjà lu : la position lui est écrite quand même
        r.UserData.SetPlayed("v", "m1", false);
        r.UserData.DenyAccess("w", "m1");        // R8

        var result = r.PositionEngine.Handle("u", "m1", 20 * Min);

        Assert.Equal(2, result.Propagated);
        Assert.Equal(20 * Min, r.UserData.GetPosition("o", "m1"));
        Assert.Equal(20 * Min, r.UserData.GetPosition("v", "m1"));
        Assert.Null(r.UserData.GetPosition("w", "m1"));
        Assert.Equal(0, r.UserData.MarkPlayedCalls);
        Assert.True(r.UserData.IsPlayed("o", "m1"));                           // le lu de « o » est intact
        Assert.Contains("no-access", r.Journal.Details("Skipped"));
        Assert.DoesNotContain("already-played", r.Journal.Details("Skipped"));
        Assert.DoesNotContain("trigger-already-played", r.Journal.Details("Skipped"));
    }

    // ================================================================= S9d : relecture d'un média déjà lu (#57)

    [Fact]
    public void S9d_ReplayOfAnAlreadyPlayedMedia_PropagatesThePauseAndTheStopPosition_TheFlagIsUntouched()
    {
        var r = new Rig();
        r.Playlist("p", "propager-avancement=OUI,propager-lu=OUI,remove-si-lu=NON", "F1");
        foreach (var user in new[] { "o", "u", "v" }) r.UserData.SetPlayed(user, "F1", true);   // F1 déjà lu par tous

        // t=1..2 : U2 (« v ») relance F1 et met en pause à 20 min -> propagée malgré l'état lu
        var pause = r.PositionEngine.Handle("v", "F1", 20 * Min);
        Assert.Equal(2, pause.Propagated);
        Assert.Equal(20 * Min, r.UserData.GetPosition("u", "F1"));
        Assert.Equal(20 * Min, r.UserData.GetPosition("o", "F1"));

        // t=3..4 : arrêt à 45 min -> propagé
        var stop = r.PositionEngine.Handle("v", "F1", 45 * Min);
        Assert.Equal(2, stop.Propagated);
        Assert.Equal(45 * Min, r.UserData.GetPosition("u", "F1"));

        Assert.Equal(0, r.UserData.MarkPlayedCalls);                           // ni Played ni PlayCount écrits
        Assert.Equal(new[] { "PositionPropagation", "PositionPropagation" }, r.Journal.Entries.Select(e => e.Kind).Where(k => k == "PositionPropagation"));
        Assert.DoesNotContain("trigger-already-played", r.Journal.Details("Skipped"));   // ce Skipped n'est plus émis
    }

    [Fact]
    public void S9d_Step5_TheReplayEnd_IsNoTransition_ForTheReadFlow_ButTheFinalStopPositionIsPropagated()
    {
        var r = new Rig();
        var s = r.Playlist("p", "propager-avancement=OUI,propager-lu=OUI,remove-si-lu=OUI", "F1");
        foreach (var user in new[] { "o", "u", "v" }) r.UserData.SetPlayed(user, "F1", true);
        var tracker = new PlayedTransitionTracker();
        var readFlowCalls = 0;
        var processor = new PlaybackEventProcessor(tracker, r.WriteTracker, (uid, item) => { readFlowCalls++; return r.ReadEngine.Handle(uid, item); }, new HandlerStats());

        // U2 relit F1 jusqu'au bout : « lu » déjà connu -> AUCUNE transition (R4c) : ni retrait, ni propagation du flag.
        Assert.Equal(PlaybackEventOutcome.NoTransition, processor.Process("v", "F1", "PlaybackStart", true));
        Assert.Equal(PlaybackEventOutcome.NoTransition, processor.Process("v", "F1", "PlaybackFinished", true));
        Assert.Equal(0, readFlowCalls);
        Assert.Equal(new[] { "F1" }, s.Items);
        Assert.Equal(0, r.UserData.MarkPlayedCalls);

        // Flux avancement (indépendant) : la position d'arrêt finale est propagée (tableau B).
        var stop = r.PositionEngine.Handle("v", "F1", 99 * Min);
        Assert.Equal(2, stop.Propagated);
        Assert.Equal(99 * Min, r.UserData.GetPosition("u", "F1"));
        Assert.Equal(new[] { "F1" }, s.Items);                                 // toujours aucun retrait
    }

    // ================================================================= S9e : avancement sans lu, et lu sans avancement

    [Fact]
    public void S9e_Row1_AvancementAlone_StopPropagatesThePosition_FinishingDoesNothingForTheFlag()
    {
        var r = new Rig();
        var s = r.Playlist("p", "propager-avancement=OUI,propager-lu=NON,remove-si-lu=NON", "F1");

        r.PositionEngine.Handle("v", "F1", 30 * Min);                          // U2 arrête à 30 min
        Assert.Equal(30 * Min, r.UserData.GetPosition("u", "F1"));

        r.ReadEngine.Handle("v", "F1");                                        // U2 termine F1 (transition vers lu)
        Assert.Equal(0, r.UserData.MarkPlayedCalls);                           // tableau A : legacy
        Assert.Equal(new[] { "F1" }, s.Items);

        r.PositionEngine.Handle("v", "F1", 99 * Min);                          // seule la position d'arrêt est propagée (S9f)
        Assert.Equal(99 * Min, r.UserData.GetPosition("u", "F1"));
        Assert.False(r.UserData.IsPlayed("u", "F1") == true);
    }

    [Fact]
    public void S9e_Row2_PropagerLuAlone_StopWritesNothing_FinishingPropagatesTheFlag_NoPosition()
    {
        var r = new Rig();
        var s = r.Playlist("p", "propager-lu=OUI,propager-avancement=NON,remove-si-lu=NON", "F1");

        r.PositionEngine.Handle("v", "F1", 30 * Min);
        Assert.Equal(0, r.UserData.SetPositionCalls);                          // avancement inactif : rien

        r.ReadEngine.Handle("v", "F1");
        Assert.True(r.UserData.IsPlayed("u", "F1"));                           // lu posé (R4b)
        Assert.True(r.UserData.IsPlayed("o", "F1"));
        Assert.Equal(0, r.UserData.SetPositionCalls);                          // aucune position écrite par le plugin
        Assert.Equal(new[] { "F1" }, s.Items);
    }

    [Fact]
    public void S9e_Row3_PropagerLuAndRemoveSiLu_WithoutAvancement_RemovesAndPropagatesTheFlag()
    {
        var r = new Rig();
        var s = r.Playlist("p", "propager-lu=OUI,remove-si-lu=OUI,propager-avancement=NON", "F1");

        r.PositionEngine.Handle("v", "F1", 30 * Min);
        Assert.Equal(0, r.UserData.SetPositionCalls);

        r.ReadEngine.Handle("v", "F1");
        Assert.Empty(s.Items);
        Assert.True(r.UserData.IsPlayed("u", "F1"));
        Assert.True(r.UserData.IsPlayed("o", "F1"));
        Assert.Equal(0, r.UserData.SetPositionCalls);
    }

    // ================================================================= S9f : position de fin ; indépendance des flux, sans ordre garanti

    private const long NearEnd = (99 * 60 + 40) * 10_000_000L;   // 99 min 40 s

    [Theory]
    [InlineData(true)]    // le flux de la position passe d'abord
    [InlineData(false)]   // le flux du lu passe d'abord
    public void S9f_NearEndPosition_IsPropagatedRaw_TheReadFlowDoesNothing_WhicheverFlowRunsFirst(bool positionFirst)
    {
        var r = new Rig();
        var s = r.Playlist("p", "propager-avancement=OUI,remove-si-lu=OUI,propager-lu=NON", "F1");

        void Position() => r.PositionEngine.Handle("v", "F1", NearEnd);
        void Read() => r.ReadEngine.Handle("v", "F1");
        if (positionFirst) { Position(); Read(); } else { Read(); Position(); }

        Assert.Equal(NearEnd, r.UserData.GetPosition("u", "F1"));              // position brute écrite (aucune règle de fin appliquée par le plugin)
        Assert.Equal(NearEnd, r.UserData.GetPosition("o", "F1"));
        Assert.False(r.UserData.IsPlayed("u", "F1") == true);                  // le plugin ne pose JAMAIS le « lu » via l'avancement : U1 reste non lu
        Assert.Equal(0, r.UserData.MarkPlayedCalls);
        Assert.Equal(new[] { "F1" }, s.Items);                                 // propager-lu non active : aucun retrait (D21)
        Assert.Contains("inactive", r.Journal.Details("Skipped"));
    }

    [Fact]
    public void FlowsAreIndependent_RunningBothInParallel_GivesTheSameOutcomeAsAnyOrder()
    {
        for (var iteration = 0; iteration < 30; iteration++)
        {
            var r = new Rig();
            // Retrait DÉSACTIVÉ (remove-si-lu=NON) : sinon le retrait de F1 par le flux du lu fait disparaître la playlist candidate du flux
            // de position, et le résultat dépendrait de l'ordre (dépendance légitime du retrait, non une violation de l'indépendance).
            var s = r.Playlist("p", "propager-lu=OUI,remove-si-lu=NON,propager-avancement=OUI", "F1");
            Parallel.Invoke(
                () => r.ReadEngine.Handle("v", "F1"),
                () => r.PositionEngine.Handle("v", "F1", 40 * Min));

            Assert.Equal(new[] { "F1" }, s.Items);                             // aucun retrait (désactivé)
            Assert.True(r.UserData.IsPlayed("u", "F1"));                       // lu propagé
            Assert.True(r.UserData.IsPlayed("o", "F1"));
            Assert.Equal(40 * Min, r.UserData.GetPosition("u", "F1"));         // position propagée
            Assert.Equal(40 * Min, r.UserData.GetPosition("o", "F1"));
            Assert.Empty(r.Journal.Of("Error"));
        }
    }

    [Fact]
    public void AvancementActive_ButNotTheReadFamilies_AReadTransitionWritesNoPositionAndNoFlag()
    {
        var r = new Rig();
        var s = r.Playlist("p", "propager-avancement=OUI", "F1");
        r.ReadEngine.Handle("v", "F1");
        Assert.Equal(0, r.UserData.MarkPlayedCalls);
        Assert.Equal(0, r.UserData.SetPositionCalls);
        Assert.Equal(new[] { "F1" }, s.Items);
    }

    [Fact]
    public void ReadFamiliesActive_ButNotTheAvancement_AStopWritesNoPosition()
    {
        var r = new Rig();
        r.Playlist("p", "propager-lu=OUI,remove-si-lu=OUI", "F1");
        r.PositionEngine.Handle("v", "F1", 50 * Min);
        Assert.Equal(0, r.UserData.SetPositionCalls);
        Assert.Equal(0, r.UserData.MarkPlayedCalls);
    }
}
