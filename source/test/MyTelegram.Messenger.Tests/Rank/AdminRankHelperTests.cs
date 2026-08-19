using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Tests.Rank;

/// <summary>
/// Feature: the tag (rank) shown next to a member's name in a group.
///
/// <para>
/// A tag is at most 16 characters long and carries no emoji. Changing somebody else's tag needs the
/// manage_ranks admin right; changing your own is allowed when either the chat's default rights or
/// your own banned rights permit edit_rank — a chat whose rights were never configured keeps tags
/// admin-only. See https://corefork.telegram.org/api/rank
/// </para>
/// </summary>
public class AdminRankHelperTests
{
    [Fact]
    public void An_absent_tag_is_valid()
    {
        Should.NotThrow(() => AdminRankHelper.ValidateOrThrow(null));
        Should.NotThrow(() => AdminRankHelper.ValidateOrThrow(string.Empty));
    }

    [Fact]
    public void A_tag_of_the_maximum_length_is_valid()
    {
        Should.NotThrow(() => AdminRankHelper.ValidateOrThrow(new string('a', AdminRankHelper.MaxRankLength)));
    }

    [Fact]
    public void A_tag_longer_than_the_maximum_is_rejected()
    {
        var exception = Should.Throw<RpcException>(
            () => AdminRankHelper.ValidateOrThrow(new string('a', AdminRankHelper.MaxRankLength + 1)));

        exception.RpcError.Message.ShouldBe("ADMIN_RANK_INVALID");
    }

    [Theory]
    [InlineData("boss 😎")]   // emoji outside the BMP, stored as a surrogate pair
    [InlineData("boss ⚽")]   // older BMP emoji
    public void A_tag_containing_an_emoji_is_rejected(string rank)
    {
        var exception = Should.Throw<RpcException>(() => AdminRankHelper.ValidateOrThrow(rank));

        exception.RpcError.Message.ShouldBe("ADMIN_RANK_EMOJI_NOT_ALLOWED");
    }

    [Fact]
    public void A_chat_that_never_configured_its_rights_keeps_tags_admin_only()
    {
        AdminRankHelper.CanEditOwnRank(null, null).ShouldBeFalse();
    }

    [Fact]
    public void The_chat_default_rights_can_open_edit_rank_for_everyone()
    {
        AdminRankHelper.CanEditOwnRank(Rights(editRank: false), null).ShouldBeTrue();
    }

    [Fact]
    public void The_chat_default_rights_can_forbid_edit_rank()
    {
        AdminRankHelper.CanEditOwnRank(Rights(editRank: true), null).ShouldBeFalse();
    }

    [Fact]
    public void A_members_own_rights_can_open_edit_rank_while_the_chat_default_forbids_it()
    {
        AdminRankHelper.CanEditOwnRank(Rights(editRank: true), Rights(editRank: false)).ShouldBeTrue();
    }

    [Fact]
    public void A_member_restricted_from_edit_rank_cannot_change_their_own_tag()
    {
        AdminRankHelper.CanEditOwnRank(null, Rights(editRank: true)).ShouldBeFalse();
    }

    private static ChatBannedRights Rights(bool editRank)
    {
        var rights = ChatBannedRights.CreateDefaultBannedRights();
        rights.EditRank = editRank;

        return rights;
    }
}
