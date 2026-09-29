using EmbySharedPlaylist.Core;
using Xunit;

namespace EmbySharedPlaylist.Tests;

public class PluginWriteTrackerTests
{
    [Fact]
    public void TryConsume_ReturnsTrueOnceForRegisteredPair()
    {
        var t = new PluginWriteTracker();
        t.Register("u1", "i10");
        Assert.True(t.TryConsume("u1", "i10"));
        Assert.False(t.TryConsume("u1", "i10"));
    }

    [Fact]
    public void TryConsume_ReturnsFalseForUnknownOrDifferentPair()
    {
        var t = new PluginWriteTracker();
        t.Register("u1", "i10");
        Assert.False(t.TryConsume("u2", "i10"));
        Assert.False(t.TryConsume("u1", "i11"));
        Assert.Equal(1, t.Count);
    }

    [Fact]
    public void Entries_ExpireAfterTtl()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t = new PluginWriteTracker(TimeSpan.FromMinutes(1), now: () => now);
        t.Register("u1", "i10");
        now = now.AddSeconds(61);
        Assert.False(t.TryConsume("u1", "i10"));
        Assert.Equal(0, t.Count);
    }

    [Fact]
    public void Entries_DoNotExpireBeforeTtl()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t = new PluginWriteTracker(TimeSpan.FromMinutes(1), now: () => now);
        t.Register("u1", "i10");
        now = now.AddSeconds(59);
        Assert.True(t.TryConsume("u1", "i10"));
    }

    [Fact]
    public void Register_EvictsOldestWhenCapacityReached()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t = new PluginWriteTracker(TimeSpan.FromHours(1), capacity: 2, now: () => now);
        t.Register("u1", "i1"); now = now.AddSeconds(1);
        t.Register("u2", "i2"); now = now.AddSeconds(1);
        t.Register("u3", "i3");
        Assert.Equal(2, t.Count);
        Assert.False(t.TryConsume("u1", "i1"));
        Assert.True(t.TryConsume("u2", "i2"));
        Assert.True(t.TryConsume("u3", "i3"));
    }

    [Fact]
    public void Register_SamePairTwice_IsACounter_TwoConsumesTrueThenFalse()
    {
        // v1.2.0 (B12, décision du teamleader) : compteur par écriture — CHANGED par rapport à « une seule entrée par couple ».
        var t = new PluginWriteTracker();
        t.Register("u1", "i1"); t.Register("u1", "i1");
        Assert.Equal(2, t.Count);                    // total des inscriptions en attente, plus le nombre de couples
        Assert.True(t.TryConsume("u1", "i1"));
        Assert.Equal(1, t.Count);
        Assert.True(t.TryConsume("u1", "i1"));
        Assert.False(t.TryConsume("u1", "i1"));
        Assert.Equal(0, t.Count);
    }

    [Fact]
    public void PositionThenPlayedOnTheSamePair_BothEchoesAreRecognisedAsPluginOrigin()
    {
        // Position propagée puis lu propagé chez le même membre (deux écritures, deux échos) : les DEUX sont d'origine plugin.
        var t = new PluginWriteTracker();
        t.Register("u1", "i1");   // écriture de position (R10)
        t.Register("u1", "i1");   // écriture du lu (R4b)
        Assert.True(t.TryConsume("u1", "i1"));
        Assert.True(t.TryConsume("u1", "i1"));
        Assert.False(t.TryConsume("u1", "i1"));      // un troisième événement serait une action utilisateur
    }

    [Fact]
    public void Unregister_CancelsOneRegistration_NoDanglingEcho()
    {
        var t = new PluginWriteTracker();
        t.Register("u1", "i1"); t.Register("u1", "i1");
        t.Unregister("u1", "i1");                    // une écriture n'a finalement pas eu lieu
        Assert.Equal(1, t.Count);
        Assert.True(t.TryConsume("u1", "i1"));
        Assert.False(t.TryConsume("u1", "i1"));
    }

    [Fact]
    public void Unregister_OfAnUnknownPair_IsANoOp_NeverNegative()
    {
        var t = new PluginWriteTracker();
        t.Unregister("u1", "i1");
        Assert.Equal(0, t.Count);
        t.Register("u1", "i1");
        t.Unregister("u1", "i1"); t.Unregister("u1", "i1");
        Assert.Equal(0, t.Count);
        Assert.False(t.TryConsume("u1", "i1"));
    }

    [Fact]
    public void Ttl_ExpiresTheWholeCounterOfAPair()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t = new PluginWriteTracker(TimeSpan.FromMinutes(1), now: () => now);
        t.Register("u1", "i1"); t.Register("u1", "i1");
        now = now.AddSeconds(61);
        Assert.False(t.TryConsume("u1", "i1"));
        Assert.Equal(0, t.Count);
    }

    [Fact]
    public void CountIsTheTotalOfPendingRegistrations_NotTheNumberOfPairs()
    {
        var t = new PluginWriteTracker();
        t.Register("u1", "i1"); t.Register("u1", "i1"); t.Register("u2", "i1");
        Assert.Equal(3, t.Count);
    }

    [Fact]
    public void Constructor_RejectsInvalidCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PluginWriteTracker(capacity: 0));
    }

    [Theory]
    [InlineData("", "i1")]
    [InlineData("u1", "")]
    [InlineData(null, "i1")]
    [InlineData("u1", null)]
    public void Register_RejectsEmptyOrNullIds(string? userId, string? itemId)
    {
        Assert.Throws<ArgumentException>(() => new PluginWriteTracker().Register(userId!, itemId!));
    }

    [Fact]
    public void UserIdsAreGuidLikeStrings_NotNumbers()
    {
        // Les ids d'utilisateur Emby sont des GUID hexadécimaux sans tirets (string), pas des entiers (#21).
        var t = new PluginWriteTracker();
        var userId = "cc25dec811d44342a22374d72e2c8827";
        t.Register(userId, "289991");
        Assert.True(t.TryConsume(userId, "289991"));
    }

    [Fact]
    public void Tracker_IsThreadSafe()
    {
        var t = new PluginWriteTracker(capacity: 50);
        Parallel.For(0, 1000, i => { t.Register("u" + i % 10, "i" + i); t.TryConsume("u" + i % 10, "i" + (i - 1)); });
        Assert.True(t.Count <= 50);
    }

    [Fact]
    public void Register_OnAnExistingKeyAtFullCapacity_NeverEvictsAnotherPair_m4()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t = new PluginWriteTracker(TimeSpan.FromHours(1), capacity: 2, now: () => now);
        t.Register("u1", "i1"); now = now.AddSeconds(1);
        t.Register("u2", "i2"); now = now.AddSeconds(1);
        t.Register("u2", "i2");                            // clé EXISTANTE à capacité pleine : simple incrément
        Assert.True(t.TryConsume("u1", "i1"));             // le plus ancien n'a PAS été évincé
        Assert.True(t.TryConsume("u2", "i2"));
        Assert.True(t.TryConsume("u2", "i2"));
        Assert.False(t.TryConsume("u2", "i2"));
    }

    [Fact]
    public void Register_OnANewKeyAtFullCapacity_StillEvictsTheOldest_m4()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t = new PluginWriteTracker(TimeSpan.FromHours(1), capacity: 2, now: () => now);
        t.Register("u1", "i1"); now = now.AddSeconds(1);
        t.Register("u2", "i2"); now = now.AddSeconds(1);
        t.Register("u3", "i3");
        Assert.False(t.TryConsume("u1", "i1"));
        Assert.True(t.TryConsume("u2", "i2"));
        Assert.True(t.TryConsume("u3", "i3"));
    }
}
