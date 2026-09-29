using System.Diagnostics;
using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.UserPage;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>
/// Tests de SPÉCIFICATION du délai du verrou de création <c>create:&lt;demandeur&gt;</c> (v1.2.0, audit sécurité M2, décision utilisateur) :
/// au plus <b>1 seconde</b> d'attente avant 409 <c>busy</c> (constante nommée <c>UserPlaylistService.CreateLockTimeout</c>, distincte du
/// délai de 5 s des verrous de playlist), plus aucun blocage de 5 s. Ces tests construisent le service SANS délai explicite pour
/// exercer la valeur par défaut réelle.
/// </summary>
public class UserPlaylistServiceCreateLockSpecTests : UserPlaylistServiceSpecBase
{
    [Fact]
    public void TheCreationLockTimeout_IsANamedConstant_OneSecond()
    {
        var field = typeof(UserPlaylistService).GetField("CreateLockTimeout");
        Assert.NotNull(field);
        Assert.Equal(TimeSpan.FromSeconds(1), (TimeSpan)field!.GetValue(null)!);
    }

    [Fact]
    public void ThePlaylistLockTimeout_IsUnchanged_FiveSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), PlaylistLocks.DefaultTimeout);
    }

    [Fact]
    public void ABusyCreationLock_ReturnsBusyAfterAboutOneSecond_NeverFive_WithTheDefaultTimeout()
    {
        var r = new Rig();
        // Service à délai PAR DÉFAUT (aucun lockTimeout explicite) : le délai de création vaut la constante (1 s).
        var service = new UserPlaylistService(r.Shares, r.Users, r.Playlists, r.Locks, r.Defaults, r.Journal, r.Clock);
        using var inside = new ManualResetEventSlim();
        r.Playlists.OnCreate = () => { inside.Set(); Thread.Sleep(3000); };
        var first = Task.Run(() => service.CreatePlaylist(Owner, "Premiere"));
        Assert.True(inside.Wait(TimeSpan.FromSeconds(5)));
        r.Playlists.OnCreate = null;

        var sw = Stopwatch.StartNew();
        var second = service.CreatePlaylist(Owner, "Seconde");
        sw.Stop();

        Assert.Equal(UserPageErrors.Busy, second.Error);
        Assert.InRange(sw.ElapsedMilliseconds, 800, 2500);      // ~1 s, jamais ~5 s
        Assert.True(first.Result.Ok);
        Assert.Equal(1, r.Playlists.CreateCalls);
    }

    [Fact]
    public void ABriefContention_UnderOneSecond_WaitsAndSucceeds_NoBusy()
    {
        var r = new Rig();
        var service = new UserPlaylistService(r.Shares, r.Users, r.Playlists, r.Locks, r.Defaults, r.Journal, r.Clock);
        using var inside = new ManualResetEventSlim();
        r.Playlists.OnCreate = () => { inside.Set(); Thread.Sleep(300); };
        var first = Task.Run(() => service.CreatePlaylist(Owner, "Premiere"));
        Assert.True(inside.Wait(TimeSpan.FromSeconds(5)));
        r.Playlists.OnCreate = null;

        var second = service.CreatePlaylist(Owner, "Seconde");

        Assert.True(second.Ok, second.Error);
        Assert.True(first.Result.Ok);
    }
}
