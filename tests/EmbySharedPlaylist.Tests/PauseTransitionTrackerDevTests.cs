using EmbySharedPlaylist.Engine;
using Xunit;

namespace EmbySharedPlaylist.Tests;

public class PauseTransitionTrackerDevTests
{
    [Fact]
    public void UnknownToPaused_IsATransition()
    {
        var t = new PauseTransitionTracker();
        Assert.True(t.OnProgress("u", "i", true));
    }

    [Fact]
    public void PausedThenPausedAgain_IsNotATransitionTheSecondTime()
    {
        var t = new PauseTransitionTracker();
        Assert.True(t.OnProgress("u", "i", true));
        Assert.False(t.OnProgress("u", "i", true));
    }

    [Fact]
    public void PlayingIsNeverATransition_AndIsRemembered()
    {
        var t = new PauseTransitionTracker();
        Assert.False(t.OnProgress("u", "i", false));
        Assert.True(t.OnProgress("u", "i", true)); // connu faux -> transition à la pause suivante
    }

    [Fact]
    public void ResumeThenPauseAgain_IsANewTransition()
    {
        var t = new PauseTransitionTracker();
        t.OnProgress("u", "i", true);
        Assert.False(t.OnProgress("u", "i", false)); // reprise : jamais une transition
        Assert.True(t.OnProgress("u", "i", true));   // nouvelle pause : transition
    }

    [Fact]
    public void UsersAndItems_DoNotInterfere()
    {
        var t = new PauseTransitionTracker();
        Assert.True(t.OnProgress("u1", "i1", true));
        Assert.True(t.OnProgress("u2", "i1", true));
        Assert.True(t.OnProgress("u1", "i2", true));
        Assert.False(t.OnProgress("u1", "i1", true));
    }

    [Fact]
    public void Capacity_ForgetsTheLeastRecentlyUpdatedPair()
    {
        var t = new PauseTransitionTracker(2);
        t.OnProgress("u", "1", true);
        t.OnProgress("u", "2", true);
        t.OnProgress("u", "1", true); // faux (déjà transitionné) mais rafraîchit 1 (LRU)
        t.OnProgress("u", "3", true); // évince 2
        Assert.Equal(2, t.Count);
        Assert.False(t.OnProgress("u", "1", true)); // toujours connu
        Assert.True(t.OnProgress("u", "2", true));   // oublié : inconnu = transition
    }

    [Fact]
    public void IsThreadSafe_AndBounded()
    {
        var t = new PauseTransitionTracker(50);
        Parallel.For(0, 2000, i => t.OnProgress("u" + i % 7, "i" + i, i % 2 == 0));
        Assert.True(t.Count <= 50);
    }

    [Fact]
    public void Constructor_RejectsInvalidCapacity() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PauseTransitionTracker(0));

    [Fact]
    public void DistinctFromPlayedTransitionTracker_NoSharedState()
    {
        // Rappel de conception (D-b) : classes distinctes, aucun couplage. Vérifié par le typage seul (pas de cast possible).
        Assert.False(typeof(PauseTransitionTracker).IsAssignableFrom(typeof(PlayedTransitionTracker)));
        Assert.False(typeof(PlayedTransitionTracker).IsAssignableFrom(typeof(PauseTransitionTracker)));
    }
}
