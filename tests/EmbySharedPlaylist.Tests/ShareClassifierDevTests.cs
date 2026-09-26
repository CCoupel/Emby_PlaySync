using EmbySharedPlaylist.Emby;
using MediaBrowser.Model.Dto;
using Xunit;

namespace EmbySharedPlaylist.Tests;

public class ShareClassifierDevTests
{
    private static ShareClassifier.Result C(params (string, UserItemShareLevel)[] rows) => ShareClassifier.Classify(rows);

    [Fact]
    public void OwnerIsTheManageDeleteRow_MembersAreEveryoneAtLeastRead()
    {
        var r = C(("o", UserItemShareLevel.ManageDelete), ("u2", UserItemShareLevel.Write), ("u3", UserItemShareLevel.Read));
        Assert.Equal("o", r.OwnerId);
        Assert.Equal(new[] { "o", "u2", "u3" }, r.MemberIds);
        Assert.True(r.IsShared);
    }

    [Fact]
    public void RowWithLevelNone_IsNotAMember()
    {
        var r = C(("o", UserItemShareLevel.ManageDelete), ("u2", UserItemShareLevel.None));
        Assert.Equal(new[] { "o" }, r.MemberIds);
        Assert.False(r.IsShared);
    }

    [Fact]
    public void NoRows_PublicOrPrivatePlaylist_IsNeverManaged()
    {
        var r = C();
        Assert.Null(r.OwnerId);
        Assert.Empty(r.MemberIds);
        Assert.False(r.IsShared);
    }

    [Fact]
    public void UnknownOwner_IsNotManaged_EvenWithMembers()
    {
        var r = C(("u2", UserItemShareLevel.Write), ("u3", UserItemShareLevel.Read));
        Assert.Null(r.OwnerId);
        Assert.False(r.IsShared);
    }

    [Fact]
    public void OwnerAlone_IsNotShared()
    {
        Assert.False(C(("o", UserItemShareLevel.ManageDelete)).IsShared);
    }

    [Fact]
    public void ManageMember_IsAMemberButNotTheOwner()
    {
        var r = C(("o", UserItemShareLevel.ManageDelete), ("m", UserItemShareLevel.Manage));
        Assert.Equal("o", r.OwnerId);
        Assert.Contains("m", r.MemberIds);
    }

    [Fact]
    public void DuplicateRowsForTheSameUser_AreCollapsed()
    {
        var r = C(("o", UserItemShareLevel.ManageDelete), ("u2", UserItemShareLevel.Read), ("u2", UserItemShareLevel.Write));
        Assert.Equal(new[] { "o", "u2" }, r.MemberIds);
    }

    [Fact]
    public void ReadOnlyMember_CountsAsAMember()
    {
        Assert.True(C(("o", UserItemShareLevel.ManageDelete), ("u3", UserItemShareLevel.Read)).IsShared);
    }
}
