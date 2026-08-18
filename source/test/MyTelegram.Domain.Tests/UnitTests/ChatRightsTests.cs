using MyTelegram.Messenger.Helpers;
using MyTelegram.ReadModel.Interfaces;

namespace MyTelegram.Domain.Tests.UnitTests;

/// <summary>
/// Admin, banned and default rights: flag round-trips and the until_date semantics described by
/// https://corefork.telegram.org/api/rights
/// </summary>
public class ChatRightsTests
{
    [Fact]
    public void AdminRights_RoundTripsEveryFlag()
    {
        var rights = ChatAdminRights.GetCreatorRights();
        rights.Anonymous = true;

        var restored = new ChatAdminRights(rights.GetFlags().ToInt32());

        restored.ChangeInfo.ShouldBeTrue();
        restored.PostMessages.ShouldBeTrue();
        restored.EditMessages.ShouldBeTrue();
        restored.DeleteMessages.ShouldBeTrue();
        restored.BanUsers.ShouldBeTrue();
        restored.InviteUsers.ShouldBeTrue();
        restored.PinMessages.ShouldBeTrue();
        restored.AddAdmins.ShouldBeTrue();
        restored.Anonymous.ShouldBeTrue();
        restored.ManageCall.ShouldBeTrue();
        restored.Other.ShouldBeTrue();
        restored.ManageTopics.ShouldBeTrue();
        restored.PostStories.ShouldBeTrue();
        restored.EditStories.ShouldBeTrue();
        restored.DeleteStories.ShouldBeTrue();
        restored.ManageDirectMessages.ShouldBeTrue();
        restored.ManageRanks.ShouldBeTrue();
    }

    [Fact]
    public void AdminRights_ManageRanks_UsesFlag18()
    {
        var rights = new ChatAdminRights { ManageRanks = true };

        rights.GetFlags().ToInt32().ShouldBe(1 << 18);
        new ChatAdminRights(1 << 18).ManageRanks.ShouldBeTrue();
    }

    [Fact]
    public void BannedRights_EditRank_UsesFlag26()
    {
        var rights = ChatBannedRights.FromValue(1 << 26, int.MaxValue);

        rights.EditRank.ShouldBeTrue();
        rights.ToIntValue().ShouldBe(1 << 26);
    }

    [Fact]
    public void BannedRights_RoundTripsEveryFlag()
    {
        // Every documented flag of chatBannedRights: bits 0-8, 10, 15, 17-26.
        var flags = 0;
        foreach (var bit in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 15, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26 })
        {
            flags |= 1 << bit;
        }

        ChatBannedRights.FromValue(flags, int.MaxValue).ToIntValue().ShouldBe(flags);
    }

    [Theory]
    // Less than 30 seconds away, or more than 366 days away, means "forever".
    [InlineData(29, int.MaxValue)]
    [InlineData(366 * 24 * 60 * 60 + 1, int.MaxValue)]
    [InlineData(0, int.MaxValue)]
    [InlineData(-5, int.MaxValue)]
    // Anything in between is kept as given.
    [InlineData(31, 1_000_031)]
    [InlineData(60 * 60, 1_003_600)]
    public void NormalizeUntilDate_TreatsOutOfRangeValuesAsForever(int offsetSeconds, int expected)
    {
        const int now = 1_000_000;
        var untilDate = offsetSeconds <= 0 ? offsetSeconds : now + offsetSeconds;

        ChatBannedRights.NormalizeUntilDate(untilDate, now).ShouldBe(expected);
    }

    [Fact]
    public void IsExpired_OnlyForATimedRestrictionInThePast()
    {
        const int now = 1_000_000;

        ChatBannedRights.FromValue(1 << 1, now - 1).IsExpired(now).ShouldBeTrue();
        ChatBannedRights.FromValue(1 << 1, now + 60).IsExpired(now).ShouldBeFalse();
        ChatBannedRights.FromValue(1 << 1, int.MaxValue).IsExpired(now).ShouldBeFalse();
        ChatBannedRights.FromValue(1 << 1, 0).IsExpired(now).ShouldBeFalse();
    }

    [Fact]
    public void EffectiveBannedRights_AreDroppedOnceTheUntilDateHasPassed()
    {
        const int now = 1_000_000;

        BannedRightsHelper.GetEffectiveBannedRights(Member(1 << 1, now - 1), now).ShouldBeNull();
        BannedRightsHelper.GetEffectiveBannedRights(Member(1 << 1, now + 60), now).ShouldNotBeNull();
        BannedRightsHelper.GetEffectiveBannedRights(Member(0, int.MaxValue), now).ShouldBeNull();
        BannedRightsHelper.GetEffectiveBannedRights(null, now).ShouldBeNull();
    }

    [Fact]
    public void IsCurrentlyKicked_FollowsTheUntilDate()
    {
        const int now = 1_000_000;
        var viewMessages = 1 << 0;

        BannedRightsHelper.IsCurrentlyKicked(Member(viewMessages, now + 60), now).ShouldBeTrue();
        BannedRightsHelper.IsCurrentlyKicked(Member(viewMessages, now - 1), now).ShouldBeFalse();
        // Restricted but not banned: not kicked.
        BannedRightsHelper.IsCurrentlyKicked(Member(1 << 1, int.MaxValue), now).ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Owner")]
    [InlineData("1234567890123456")]
    public void AdminRank_AcceptsShortPlainText(string? rank)
    {
        Should.NotThrow(() => AdminRankHelper.ValidateOrThrow(rank));
    }

    [Fact]
    public void AdminRank_RejectsTooLongRank()
    {
        var exception = Should.Throw<RpcException>(() => AdminRankHelper.ValidateOrThrow("12345678901234567"));

        exception.RpcError.Message.ShouldBe("ADMIN_RANK_INVALID");
    }

    [Fact]
    public void AdminRank_RejectsEmoji()
    {
        var exception = Should.Throw<RpcException>(() => AdminRankHelper.ValidateOrThrow("boss 😀"));

        exception.RpcError.Message.ShouldBe("ADMIN_RANK_EMOJI_NOT_ALLOWED");
    }

    private static IChannelMemberReadModel Member(int bannedRights, int untilDate)
    {
        var member = new Mock<IChannelMemberReadModel>();
        member.SetupGet(p => p.BannedRights).Returns(bannedRights);
        member.SetupGet(p => p.UntilDate).Returns(untilDate);

        return member.Object;
    }
}
