using EmbySharedPlaylist.Engine;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// Tests de SPÉCIFICATION de la détection de transition « non lu -> lu » (D-a, Q1, issue #12), écrits par scénario de lecture
/// (séquence d'événements UserDataSaved), indépendamment des tests boîte blanche <see cref="PlayedTransitionTrackerDevTests"/>.
/// Chaque étape : « motif,played,attendu » ; « attendu » = l'événement est une transition à traiter.
/// </summary>
public class PlayedTransitionTrackerSpecTests
{
    private static void Replay(string script, PlayedTransitionTracker? tracker = null, string user = "u", string item = "i")
    {
        var t = tracker ?? new PlayedTransitionTracker();
        var n = 0;
        foreach (var step in script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            n++;
            var p = step.Split(',');
            string? reason = p[0] == "null" ? null : p[0];
            var played = bool.Parse(p[1]);
            var expected = bool.Parse(p[2]);
            Assert.True(expected == t.OnUserData(user, item, reason, played), $"étape {n} ({step}) : transition attendue = {expected}");
        }
    }

    [Theory]
    [InlineData("film regardé normalement", "PlaybackStart,false,false;PlaybackProgress,false,false;PlaybackFinished,true,true;PlaybackFinished,true,false")]
    [InlineData("Q1 : relecture d'un média déjà lu jusqu'au bout", "PlaybackStart,true,false;PlaybackProgress,true,false;PlaybackFinished,true,false")]
    [InlineData("Q1 : décocher puis recocher « lu »", "TogglePlayed,true,true;TogglePlayed,false,false;TogglePlayed,true,true")]
    [InlineData("TogglePlayed=true est toujours une transition certaine", "TogglePlayed,true,true;TogglePlayed,true,true")]
    [InlineData("Progress avec played=true, dernière valeur inconnue", "PlaybackProgress,true,true;PlaybackProgress,true,false")]
    [InlineData("Progress avec played=false puis played=true", "PlaybackProgress,false,false;PlaybackProgress,true,true")]
    [InlineData("A1 : motif inconnu (null), mémoire inconnue => mémorisé sans transition", "null,true,false;PlaybackFinished,true,false")]
    [InlineData("A1 : Import, mémoire inconnue => mémorisé sans transition", "Import,true,false;Import,true,false")]
    [InlineData("A1 : autre motif avec mémoire « non lu » => vrai changement, transition", "PlaybackProgress,false,false;Import,true,true")]
    [InlineData("A1 : UpdateUserRating après TogglePlayed(false) => transition", "TogglePlayed,false,false;UpdateUserRating,true,true")]
    [InlineData("motif d'un autre type après lecture (Import)", "PlaybackFinished,true,true;Import,true,false")]
    [InlineData("casse ignorée", "playbackfinished,true,true;PLAYBACKFINISHED,true,false")]
    [InlineData("PlaybackStart played=false puis Finished", "PlaybackStart,false,false;PlaybackFinished,true,true")]
    [InlineData("played=false n'est jamais une transition", "PlaybackProgress,false,false;TogglePlayed,false,false;PlaybackFinished,false,false")]
    public void ScenarioOfPlayback_TriggersExactlyWhenTheMediaBecomesPlayed(string label, string script)
    {
        Assert.False(string.IsNullOrEmpty(label));
        Replay(script);
    }

    [Fact]
    public void ReplayAfterAnUncheck_IsAgainATransition()
    {
        // lu, puis décoché, puis relu jusqu'au bout : la relecture repart de « non lu »
        Replay("PlaybackFinished,true,true;TogglePlayed,false,false;PlaybackStart,false,false;PlaybackFinished,true,true");
    }

    [Fact]
    public void TwoUsersOnTheSameMedia_AreIndependent()
    {
        var t = new PlayedTransitionTracker();
        Assert.True(t.OnUserData("u1", "i", "PlaybackFinished", true));
        Assert.True(t.OnUserData("u2", "i", "PlaybackFinished", true));   // le lu de u1 ne masque pas la transition de u2
        Assert.False(t.OnUserData("u1", "i", "PlaybackFinished", true));
    }

    [Fact]
    public void TwoMediaOfTheSameUser_AreIndependent()
    {
        var t = new PlayedTransitionTracker();
        Assert.True(t.OnUserData("u", "a", "PlaybackFinished", true));
        Assert.True(t.OnUserData("u", "b", "PlaybackFinished", true));
        Assert.False(t.OnUserData("u", "a", "PlaybackFinished", true));
    }

    [Fact]
    public void ProgressWithoutTransition_IsMemoryOnly_AndCountsPairsUpToTheCapacity()
    {
        var t = new PlayedTransitionTracker(3);
        for (var i = 0; i < 10; i++) Assert.False(t.OnUserData("u", "m" + i, "PlaybackProgress", false));
        Assert.Equal(3, t.Count);
    }

    [Fact]
    public void AForgottenPair_BehavesAsUnknown_SoTheNextPlayedTrueIsATransition()
    {
        var t = new PlayedTransitionTracker(1);
        Assert.True(t.OnUserData("u", "a", "PlaybackFinished", true));
        Assert.True(t.OnUserData("u", "b", "PlaybackFinished", true));     // « a » est oublié (capacité 1)
        Assert.True(t.OnUserData("u", "a", "PlaybackFinished", true));     // inconnu => transition (règle de repli documentée)
    }

    [Fact]
    public void ManyThreads_NeverLoseOrDuplicateTheTransition()
    {
        var t = new PlayedTransitionTracker();
        var wins = 0;
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            start.Wait();
            if (t.OnUserData("u", "i", "PlaybackFinished", true)) Interlocked.Increment(ref wins);
        })).ToArray();
        start.Set();
        Task.WaitAll(tasks);
        Assert.Equal(1, wins);   // une seule transition, quel que soit l'entrelacement
    }
}
