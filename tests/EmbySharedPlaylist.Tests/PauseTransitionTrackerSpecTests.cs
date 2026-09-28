using EmbySharedPlaylist.Engine;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// Tests de SPÉCIFICATION de la détection de transition « en lecture -> en pause » (#45, déclencheur
/// <c>ISessionManager.PlaybackProgress</c>), écrits par scénario de session (séquence d'événements <c>IsPaused</c>),
/// indépendamment des tests boîte blanche <see cref="PauseTransitionTrackerDevTests"/>. Même forme que
/// <see cref="PlayedTransitionTrackerSpecTests"/> : chaque étape « isPaused,attendu » ; « attendu » = l'événement est
/// une transition à traiter (jamais <c>PlaybackStopped</c>, qui ne passe pas par ce tracker : traité systématiquement
/// par <see cref="PlaybackPositionEngine"/>, hors de la portée de cette classe).
/// </summary>
public class PauseTransitionTrackerSpecTests
{
    private static void Replay(string script, PauseTransitionTracker? tracker = null, string user = "u", string item = "i")
    {
        var t = tracker ?? new PauseTransitionTracker();
        var n = 0;
        foreach (var step in script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            n++;
            var p = step.Split(',');
            var isPaused = bool.Parse(p[0]);
            var expected = bool.Parse(p[1]);
            Assert.True(expected == t.OnProgress(user, item, isPaused), $"étape {n} ({step}) : transition attendue = {expected}");
        }
    }

    [Theory]
    [InlineData("lecture normale, jamais de pause (heartbeats seulement, cf. I35)", "false,false;false,false;false,false")]
    [InlineData("pause unique en milieu de lecture", "false,false;false,false;true,true")]
    [InlineData("pause dès le premier événement reçu (état inconnu -> pause = transition, sur-déclencher plutôt que sous-déclencher)", "true,true")]
    [InlineData("le client renvoie Progress en boucle PENDANT la pause : une seule transition, les suivants sont mémoire seule", "true,true;true,false;true,false;true,false")]
    [InlineData("reprise puis nouvelle pause plus tard (S9, double arrêt) : chaque pause est une nouvelle transition", "true,true;false,false;false,false;true,true")]
    [InlineData("plusieurs allers-retours pause/reprise", "true,true;false,false;true,true;false,false;true,true")]
    public void ScenarioOfSession_TriggersExactlyOnTheFalseToTrueTransition(string label, string script)
    {
        Assert.False(string.IsNullOrEmpty(label));
        Replay(script);
    }

    [Fact]
    public void TwoUsersOnTheSameMedia_AreIndependent()
    {
        var t = new PauseTransitionTracker();
        Assert.True(t.OnProgress("u1", "i", true));
        Assert.True(t.OnProgress("u2", "i", true)); // la pause de u1 ne masque pas la transition de u2
        Assert.False(t.OnProgress("u1", "i", true));
    }

    [Fact]
    public void TwoMediaOfTheSameUser_AreIndependent()
    {
        var t = new PauseTransitionTracker();
        Assert.True(t.OnProgress("u", "a", true));
        Assert.True(t.OnProgress("u", "b", true));
        Assert.False(t.OnProgress("u", "a", true));
    }

    [Fact]
    public void AForgottenPair_BehavesAsUnknown_SoTheNextPauseIsATransition()
    {
        var t = new PauseTransitionTracker(1);
        Assert.True(t.OnProgress("u", "a", true));
        Assert.True(t.OnProgress("u", "b", true));  // « a » est oublié (capacité 1)
        Assert.True(t.OnProgress("u", "a", true));  // inconnu => transition (même règle de repli que PlayedTransitionTracker)
    }

    [Fact]
    public void ManyThreads_NeverLoseOrDuplicateTheTransition()
    {
        // Propriété de course, au-delà du simple compte borné déjà vérifié par PauseTransitionTrackerDevTests.IsThreadSafe_AndBounded :
        // même sous entrelacement, EXACTEMENT une transition doit gagner pour un couple (utilisateur, média) donné.
        var t = new PauseTransitionTracker();
        var wins = 0;
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            start.Wait();
            if (t.OnProgress("u", "i", true)) Interlocked.Increment(ref wins);
        })).ToArray();
        start.Set();
        Task.WaitAll(tasks);
        Assert.Equal(1, wins);
    }
}
