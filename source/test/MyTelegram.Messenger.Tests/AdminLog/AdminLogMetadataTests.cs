using MyTelegram.Messenger.Services.AdminLog;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.AdminLog;

/// <summary>
/// Feature: the admin log — how an event is classified when it is written.
///
/// <para>
/// Every entry stores the <a href="https://corefork.telegram.org/constructor/channelAdminLogEventsFilter">filter</a>
/// tags it belongs to, the text <c>q</c> searches in and the peers it references, so that reading the log is
/// a plain indexed lookup. A restriction change is a kick/unkick when view_messages changed and a ban/unban
/// when any other restriction did; a rights change is a promotion or a demotion, and a change limited to the
/// custom title is edit_rank. See https://corefork.telegram.org/api/recent-actions
/// </para>
/// </summary>
public class AdminLogMetadataTests
{
    private const long TargetUserId = 2010001;

    [Fact]
    public void Restricting_view_messages_is_a_kick()
    {
        var action = new TChannelAdminLogEventActionParticipantToggleBan
        {
            PrevParticipant = Participant(),
            NewParticipant = Banned(viewMessages: true)
        };

        AdminLogMetadata.Filters(action).ShouldContain(AdminLogMetadata.Kick);
    }

    [Fact]
    public void Lifting_view_messages_is_an_unkick()
    {
        var action = new TChannelAdminLogEventActionParticipantToggleBan
        {
            PrevParticipant = Banned(viewMessages: true),
            NewParticipant = Participant()
        };

        AdminLogMetadata.Filters(action).ShouldContain(AdminLogMetadata.Unkick);
    }

    [Fact]
    public void Adding_a_restriction_other_than_view_messages_is_a_ban()
    {
        var action = new TChannelAdminLogEventActionParticipantToggleBan
        {
            PrevParticipant = Participant(),
            NewParticipant = Banned(viewMessages: false, sendMessages: true)
        };

        var tags = AdminLogMetadata.Filters(action);

        tags.ShouldContain(AdminLogMetadata.Ban);
        tags.ShouldNotContain(AdminLogMetadata.Kick);
    }

    [Fact]
    public void Lifting_a_restriction_other_than_view_messages_is_an_unban()
    {
        var action = new TChannelAdminLogEventActionParticipantToggleBan
        {
            PrevParticipant = Banned(viewMessages: false, sendMessages: true),
            NewParticipant = Participant()
        };

        AdminLogMetadata.Filters(action).ShouldContain(AdminLogMetadata.Unban);
    }

    [Fact]
    public void Granting_rights_is_a_promotion_and_taking_them_away_a_demotion()
    {
        var promotion = new TChannelAdminLogEventActionParticipantToggleAdmin
        {
            PrevParticipant = Participant(),
            NewParticipant = Admin(new TChatAdminRights { BanUsers = true })
        };

        var demotion = new TChannelAdminLogEventActionParticipantToggleAdmin
        {
            PrevParticipant = Admin(new TChatAdminRights { BanUsers = true }),
            NewParticipant = Participant()
        };

        AdminLogMetadata.Filters(promotion).ShouldContain(AdminLogMetadata.Promote);
        AdminLogMetadata.Filters(demotion).ShouldContain(AdminLogMetadata.Demote);
    }

    [Fact]
    public void A_change_limited_to_the_custom_title_is_edit_rank()
    {
        var rights = new TChatAdminRights { BanUsers = true };

        var action = new TChannelAdminLogEventActionParticipantToggleAdmin
        {
            PrevParticipant = Admin(rights, rank: "moderator"),
            NewParticipant = Admin(rights, rank: "janitor")
        };

        var tags = AdminLogMetadata.Filters(action);

        tags.ShouldContain(AdminLogMetadata.EditRank);
        tags.ShouldNotContain(AdminLogMetadata.Promote);
        tags.ShouldNotContain(AdminLogMetadata.Demote);
    }

    [Theory]
    [InlineData(typeof(TChannelAdminLogEventActionChangeTitle), AdminLogMetadata.Info)]
    [InlineData(typeof(TChannelAdminLogEventActionChangeAbout), AdminLogMetadata.Info)]
    [InlineData(typeof(TChannelAdminLogEventActionChangeUsernames), AdminLogMetadata.Info)]
    [InlineData(typeof(TChannelAdminLogEventActionChangeHistoryTTL), AdminLogMetadata.Info)]
    [InlineData(typeof(TChannelAdminLogEventActionChangeAvailableReactions), AdminLogMetadata.Info)]
    [InlineData(typeof(TChannelAdminLogEventActionToggleAntiSpam), AdminLogMetadata.Settings)]
    [InlineData(typeof(TChannelAdminLogEventActionToggleAutotranslation), AdminLogMetadata.Settings)]
    [InlineData(typeof(TChannelAdminLogEventActionToggleNoForwards), AdminLogMetadata.Settings)]
    [InlineData(typeof(TChannelAdminLogEventActionToggleSignatureProfiles), AdminLogMetadata.Settings)]
    [InlineData(typeof(TChannelAdminLogEventActionDefaultBannedRights), AdminLogMetadata.Settings)]
    [InlineData(typeof(TChannelAdminLogEventActionUpdatePinned), AdminLogMetadata.Pinned)]
    [InlineData(typeof(TChannelAdminLogEventActionEditMessage), AdminLogMetadata.Edit)]
    [InlineData(typeof(TChannelAdminLogEventActionStopPoll), AdminLogMetadata.Edit)]
    [InlineData(typeof(TChannelAdminLogEventActionDeleteMessage), AdminLogMetadata.Delete)]
    [InlineData(typeof(TChannelAdminLogEventActionSendMessage), AdminLogMetadata.Send)]
    [InlineData(typeof(TChannelAdminLogEventActionParticipantJoin), AdminLogMetadata.Join)]
    [InlineData(typeof(TChannelAdminLogEventActionParticipantLeave), AdminLogMetadata.Leave)]
    [InlineData(typeof(TChannelAdminLogEventActionParticipantInvite), AdminLogMetadata.Invite)]
    [InlineData(typeof(TChannelAdminLogEventActionParticipantSubExtend), AdminLogMetadata.SubExtend)]
    [InlineData(typeof(TChannelAdminLogEventActionParticipantEditRank), AdminLogMetadata.EditRank)]
    [InlineData(typeof(TChannelAdminLogEventActionStartGroupCall), AdminLogMetadata.GroupCall)]
    [InlineData(typeof(TChannelAdminLogEventActionParticipantMute), AdminLogMetadata.GroupCall)]
    [InlineData(typeof(TChannelAdminLogEventActionParticipantVolume), AdminLogMetadata.GroupCall)]
    [InlineData(typeof(TChannelAdminLogEventActionExportedInviteEdit), AdminLogMetadata.Invites)]
    [InlineData(typeof(TChannelAdminLogEventActionExportedInviteRevoke), AdminLogMetadata.Invites)]
    [InlineData(typeof(TChannelAdminLogEventActionExportedInviteDelete), AdminLogMetadata.Invites)]
    [InlineData(typeof(TChannelAdminLogEventActionCreateTopic), AdminLogMetadata.Forums)]
    [InlineData(typeof(TChannelAdminLogEventActionEditTopic), AdminLogMetadata.Forums)]
    [InlineData(typeof(TChannelAdminLogEventActionPinTopic), AdminLogMetadata.Forums)]
    [InlineData(typeof(TChannelAdminLogEventActionToggleForum), AdminLogMetadata.Forums)]
    public void Every_action_lands_in_the_filter_the_client_offers(Type actionType, string expectedTag)
    {
        var action = (IChannelAdminLogEventAction)Activator.CreateInstance(actionType)!;

        AdminLogMetadata.Filters(action).ShouldContain(expectedTag);
    }

    [Fact]
    public void A_join_through_an_invite_link_is_both_a_join_and_an_invite_link_event()
    {
        var action = new TChannelAdminLogEventActionParticipantJoinByInvite();

        var tags = AdminLogMetadata.Filters(action);

        tags.ShouldContain(AdminLogMetadata.Join);
        tags.ShouldContain(AdminLogMetadata.Invites);
    }

    [Fact]
    public void The_search_text_holds_the_message_text()
    {
        var action = new TChannelAdminLogEventActionDeleteMessage
        {
            Message = new TMessage { Id = 1, Message = "Secret Announcement" }
        };

        AdminLogMetadata.SearchText(action).ShouldBe("secret announcement");
    }

    [Fact]
    public void The_search_text_holds_both_sides_of_a_title_change()
    {
        var action = new TChannelAdminLogEventActionChangeTitle
        {
            PrevValue = "Old Name",
            NewValue = "New Name"
        };

        AdminLogMetadata.SearchText(action).ShouldBe("old name new name");
    }

    [Fact]
    public void The_restricted_participant_is_reported_as_a_referenced_user()
    {
        var action = new TChannelAdminLogEventActionParticipantToggleBan
        {
            PrevParticipant = Participant(),
            NewParticipant = Banned(viewMessages: true)
        };

        AdminLogMetadata.RelatedUserIds(action).ShouldBe([TargetUserId]);
    }

    [Fact]
    public void The_admin_who_approved_a_join_request_is_reported_as_a_referenced_user()
    {
        var action = new TChannelAdminLogEventActionParticipantJoinByRequest
        {
            Invite = new TChatInviteExported { Link = "https://t.me/+abc" },
            ApprovedBy = 777
        };

        AdminLogMetadata.RelatedUserIds(action).ShouldBe([777L]);
    }

    [Fact]
    public void Both_sides_of_a_linked_chat_change_are_reported_as_referenced_channels()
    {
        var action = new TChannelAdminLogEventActionChangeLinkedChat
        {
            PrevValue = 111,
            NewValue = 222
        };

        AdminLogMetadata.RelatedChannelIds(action).ShouldBe([111L, 222L]);
    }

    private static IChannelParticipant Participant() =>
        new TChannelParticipant { UserId = TargetUserId, Date = 1 };

    private static IChannelParticipant Banned(bool viewMessages, bool sendMessages = false) =>
        new TChannelParticipantBanned
        {
            Peer = new TPeerUser { UserId = TargetUserId },
            KickedBy = 1,
            Date = 1,
            BannedRights = new TChatBannedRights
            {
                ViewMessages = viewMessages,
                SendMessages = sendMessages
            }
        };

    private static IChannelParticipant Admin(IChatAdminRights rights, string? rank = null) =>
        new TChannelParticipantAdmin
        {
            UserId = TargetUserId,
            AdminRights = rights,
            Rank = rank,
            PromotedBy = 1,
            Date = 1
        };
}
