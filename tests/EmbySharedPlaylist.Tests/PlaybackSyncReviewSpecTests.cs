using EmbySharedPlaylist.Engine;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// Tests de SPÉCIFICATION issus de la revue du bugfix #58 (<c>_work/reports/code-reviewer-20260930-082817.md</c>) :
/// M1 (cibles mémorisées : liste vide = à re-résoudre, péremption), M2 (<c>PlaybackSyncTracker.IsOpen</c>, garde de session),
/// m4 (PlaySessionId vide jamais « fermé » pour un Progress) et m1 (<see cref="PlaybackCompletion.LooksCompleted"/>, repli pur
/// de fin de lecture, spike U15). Complète <see cref="PlaybackSyncTrackerSpecTests"/> sans le modifier.
/// </summary>
public class PlaybackSyncReviewSpecTests
{
    private const string U = "u", I = "i", S1 = "sess-1";

    private static (PlaybackSyncTracker T, FakeClock C) Make()
    {
        var clock = new FakeClock();
        return (new PlaybackSyncTracker(clock), clock);
    }

    // ---- M1 : cibles -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void M1_EmptyTargets_AreNeverFresh_TheyMustBeReResolved()
    {
        var (t, _) = Make();
        t.OnStart(U, I, S1, Array.Empty<string>());

        Assert.Null(t.GetFreshTargets(U, I));
    }

    [Fact]
    public void M1_SetTargetsWithAnEmptyList_IsNotFresh_ThenANonEmptyResolutionIs()
    {
        var (t, _) = Make();
        t.OnStart(U, I, S1);
        t.SetTargets(U, I, Array.Empty<string>());
        Assert.Null(t.GetFreshTargets(U, I));

        t.SetTargets(U, I, new[] { "p1" });

        Assert.Equal(new[] { "p1" }, t.GetFreshTargets(U, I));
    }

    [Fact]
    public void M1_ANewResolution_RedatesTheTargets()
    {
        var (t, c) = Make();
        t.OnStart(U, I, S1, new[] { "p1" });
        c.UtcNow += PlaybackSyncTracker.TargetsMaxAge - TimeSpan.FromSeconds(1);
        t.SetTargets(U, I, new[] { "p1", "p2" });   // vraie résolution : redate
        c.UtcNow += TimeSpan.FromSeconds(30);

        Assert.Equal(new[] { "p1", "p2" }, t.GetFreshTargets(U, I));
    }

    [Fact]
    public void M1_WithoutANewResolution_TheTargetsExpireAfterTheMaxAge_RegardlessOfProgressEvents()
    {
        var (t, c) = Make();
        t.OnStart(U, I, S1, new[] { "p1" });
        for (var n = 0; n < 40; n++)
        {
            c.UtcNow += TimeSpan.FromSeconds(10);
            t.OnProgress(U, I, S1, false);   // des Progress n'y changent rien
        }

        Assert.Null(t.GetFreshTargets(U, I));   // 400 s > 5 min
    }

    // ---- M2 : IsOpen --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void IsOpen_IsTrueDuringTheSession_FalseAfterTheStop()
    {
        var (t, _) = Make();
        t.OnStart(U, I, S1);
        Assert.True(t.IsOpen(U, I, S1));

        t.OnStop(U, I, S1);

        Assert.False(t.IsOpen(U, I, S1));
    }

    [Fact]
    public void IsOpen_IsFalseForAnUnknownPair()
    {
        var (t, _) = Make();

        Assert.False(t.IsOpen(U, I, S1));
    }

    [Fact]
    public void IsOpen_IsFalseForAnotherPlaySessionId_TheOldSessionIsNotTheCurrentOne()
    {
        var (t, _) = Make();
        t.OnStart(U, I, "new");

        Assert.False(t.IsOpen(U, I, "old"));
        Assert.True(t.IsOpen(U, I, "new"));
    }

    [Fact]
    public void IsOpen_ToleratesAnEmptyOrNullPlaySessionId_OnBothSides()
    {
        var (t, _) = Make();
        t.OnProgress(U, I, null, false);

        Assert.True(t.IsOpen(U, I, null));
        Assert.True(t.IsOpen(U, I, ""));
        Assert.True(t.IsOpen(U, I, "whatever"));   // session anonyme : rien ne permet de la distinguer
    }

    [Fact]
    public void IsOpen_AProgressInFlight_SeesTheStopBetweenDecisionAndWrite()
    {
        var (t, _) = Make();
        t.OnProgress(U, I, S1, false);
        var decided = t.IsOpen(U, I, S1);      // vrai au moment de la décision
        t.OnStop(U, I, S1);

        Assert.True(decided);
        Assert.False(t.IsOpen(U, I, S1));       // faux sous les verrous, après le Stop
    }

    // ---- m4 : PlaySessionId vide -------------------------------------------------------------------------------------------------

    [Fact]
    public void M4_AClosedSessionWithAnEmptyPlaySessionId_DoesNotMuteTheNextPlayback()
    {
        var (t, c) = Make();
        t.OnProgress(U, I, null, false);
        t.OnStop(U, I, null);
        c.UtcNow += TimeSpan.FromSeconds(30);

        Assert.Equal(PlaybackSyncDecision.Periodic, t.OnProgress(U, I, null, false));   // client sans PlaySessionId ni PlaybackStart
        Assert.NotEqual(PlaybackSyncDecision.Ignored, t.OnProgress(U, I, "", false));
    }

    [Fact]
    public void M4_AnEmptyPlaySessionIdProgress_AfterANamedSessionClosed_IsNotIgnored()
    {
        var (t, _) = Make();
        t.OnProgress(U, I, S1, false);
        t.OnStop(U, I, S1);

        Assert.NotEqual(PlaybackSyncDecision.Ignored, t.OnProgress(U, I, "", false));
    }

    [Fact]
    public void M4_ANamedClosedSession_StillIgnoresItsLateProgress_CA6Preserved()
    {
        var (t, _) = Make();
        t.OnProgress(U, I, S1, false);
        t.OnStop(U, I, S1);

        Assert.Equal(PlaybackSyncDecision.Ignored, t.OnProgress(U, I, S1, false));
    }

    // ---- m1 : repli pur de fin de lecture (spike U15) ---------------------------------------------------------------------------

    [Theory]
    // runtime, arrêt, lu (déclencheur), position stockée (déclencheur) => fin de lecture ?
    [InlineData(1000L, 900L, true, 0L, true)]      // pile 90 %
    [InlineData(1000L, 1000L, true, 0L, true)]     // 100 %
    [InlineData(1000L, 990L, true, 0L, true)]
    [InlineData(1000L, 899L, true, 0L, false)]     // sous 90 %
    [InlineData(1000L, 500L, true, 0L, false)]
    [InlineData(1000L, 950L, true, 5L, false)]     // position non remise à 0 : Emby n'a pas appliqué la fin de lecture
    [InlineData(1000L, 950L, false, 0L, false)]    // pas lu
    [InlineData(1000L, 950L, null, 0L, false)]     // lu inconnu
    [InlineData(1000L, 950L, true, null, false)]   // position inconnue
    [InlineData(null, 950L, true, 0L, false)]      // durée inconnue
    [InlineData(0L, 950L, true, 0L, false)]        // durée nulle
    [InlineData(-5L, 950L, true, 0L, false)]       // durée négative
    [InlineData(1000L, null, true, 0L, false)]     // position d'arrêt inconnue
    public void LooksCompleted_Table(long? runtime, long? stop, bool? played, long? stored, bool expected)
    {
        Assert.Equal(expected, PlaybackCompletion.LooksCompleted(runtime, stop, played, stored));
    }

    [Fact]
    public void LooksCompleted_FallbackRatio_IsNinetyPercent()
    {
        Assert.Equal(0.9, PlaybackCompletion.FallbackRatio);
    }

    [Fact]
    public void LooksCompleted_RealisticTicks_NinetyOnePercentOfAHundredMinutes()
    {
        var runtime = 100 * TimeSpan.TicksPerMinute;

        Assert.True(PlaybackCompletion.LooksCompleted(runtime, 91 * TimeSpan.TicksPerMinute, true, 0));
        Assert.False(PlaybackCompletion.LooksCompleted(runtime, 89 * TimeSpan.TicksPerMinute, true, 0));
    }
}
