using Moq;
using MyTelegram.Messenger.Services;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.Peers;

/// <summary>
/// Feature: sending <a href="https://corefork.telegram.org/api/min">min constructors</a>.
///
/// <para>
/// A peer the caller has no relationship with, seen only because it turned up in somebody else's
/// message, is sent with the <c>min</c> flag and without the fields that would describe such a
/// relationship. The client keeps the context it was seen in and addresses it later through an
/// <c>input*FromMessage</c> citation rather than an access hash.
/// </para>
/// <para>
/// The reduction has to stay in step with <see cref="FromMessagePeerResolver"/>: a peer is only ever
/// reduced when the citation the client will build for it is one the server accepts back. Both sides
/// consult <see cref="MessagePeerReferences"/>, and these tests pin the cases where reducing would
/// strand the client — self, a bot caller, the chat the response is about, a private chat with no
/// citable container — as well as the fields the <c>user</c> and <c>channel</c> pages forbid a min
/// constructor from speaking for, and the identity fields it must leave alone.
/// </para>
/// </summary>
public class MinConstructorReducerTests
{
    private const long CallerUserId = 2_000_001;
    private const long BotCallerUserId = 600_000_000_010;
    private const long SenderUserId = 2_000_002;
    private const long StrangerUserId = 2_000_003;
    private const long ContainerChannelId = 800_000_000_001;
    private const long OtherChannelId = 800_000_000_002;
    private const int MsgId = 34;

    [Fact]
    public void A_sender_the_caller_does_not_know_is_reduced_to_min()
    {
        var sender = User(SenderUserId);

        Reduce([ChannelMessage()], users: [sender]);

        sender.Min.ShouldBeTrue();
    }

    [Fact]
    public void A_user_no_message_in_the_response_names_is_left_whole()
    {
        // Without a citable context the client could not address a min peer at all.
        var stranger = User(StrangerUserId);

        Reduce([ChannelMessage()], users: [stranger]);

        stranger.Min.ShouldBeFalse();
    }

    [Fact]
    public void The_caller_themselves_is_never_reduced()
    {
        var self = User(CallerUserId, self: true);

        Reduce([ChannelMessage(senderUserId: CallerUserId)], users: [self]);

        self.Min.ShouldBeFalse();
    }

    [Fact]
    public void A_contact_is_reduced_too_because_min_cannot_unset_the_contact_flags()
    {
        // Contact/mutual_contact are on the "do not apply if min" list, so the client keeps its own
        // values for them and nothing is lost by reducing a contact met in a group.
        var sender = User(SenderUserId);
        sender.Contact = true;

        Reduce([ChannelMessage()], users: [sender]);

        sender.Min.ShouldBeTrue();
    }

    [Fact]
    public void A_message_from_a_private_chat_is_not_a_citable_context()
    {
        // inputPeer*FromMessage names the container chat, and Testgram stores a private message under
        // each participant's own id, so there is no chat to name.
        var sender = User(SenderUserId);

        Reduce([PrivateMessage()], users: [sender]);

        sender.Min.ShouldBeFalse();
    }

    [Fact]
    public void A_reduced_user_loses_every_field_a_min_constructor_may_not_speak_for()
    {
        var sender = User(SenderUserId);
        sender.Phone = "+15550100";
        sender.Contact = true;
        sender.MutualContact = true;
        sender.CloseFriend = true;
        sender.AttachMenuEnabled = true;
        sender.BotCanEdit = true;
        sender.StoriesHidden = true;

        Reduce([ChannelMessage()], users: [sender]);

        sender.Phone.ShouldBeNull();
        sender.Contact.ShouldBeFalse();
        sender.MutualContact.ShouldBeFalse();
        sender.CloseFriend.ShouldBeFalse();
        sender.AttachMenuEnabled.ShouldBeFalse();
        sender.BotCanEdit.ShouldBeFalse();
        sender.StoriesHidden.ShouldBeFalse();
    }

    [Fact]
    public void A_reduced_user_keeps_the_fields_a_min_constructor_does_carry()
    {
        var sender = User(SenderUserId);
        sender.FirstName = "Ada";
        sender.Username = "ada";
        sender.AccessHash = 12345;

        Reduce([ChannelMessage()], users: [sender]);

        sender.FirstName.ShouldBe("Ada");
        sender.Username.ShouldBe("ada");
        // Still valid for inputPeerPhotoFileLocation, so avatars keep loading.
        sender.AccessHash.ShouldBe(12345);
    }

    [Fact]
    public void A_real_photo_on_a_reduced_user_is_marked_applicable()
    {
        var sender = User(SenderUserId);
        sender.Photo = new TUserProfilePhoto { PhotoId = 777 };

        Reduce([ChannelMessage()], users: [sender]);

        // Without apply_min_photo the client must ignore a min user's photo entirely.
        sender.ApplyMinPhoto.ShouldBeTrue();
    }

    [Fact]
    public void An_empty_photo_is_not_marked_applicable()
    {
        var sender = User(SenderUserId);
        sender.Photo = new TUserProfilePhotoEmpty();

        Reduce([ChannelMessage()], users: [sender]);

        sender.ApplyMinPhoto.ShouldBeFalse();
    }

    [Fact]
    public void The_chat_the_response_is_about_is_never_reduced()
    {
        // The caller asked for this history; a min channel here would leave them unable to act on the
        // chat they are reading.
        var container = Channel(ContainerChannelId);

        Reduce([ChannelMessage()], chats: [container]);

        container.Min.ShouldBeFalse();
    }

    [Fact]
    public void A_channel_only_named_by_a_forward_is_reduced()
    {
        var forwarded = Channel(OtherChannelId);
        var message = ChannelMessage(
            fwdHeader: new MessageFwdHeader { FromId = new Peer(PeerType.Channel, OtherChannelId) });

        Reduce([message], chats: [forwarded]);

        forwarded.Min.ShouldBeTrue();
    }

    [Fact]
    public void A_channel_the_caller_belongs_to_is_reduced_too()
    {
        // Membership shows through left/banned_rights/participants_count, and all three are fields the
        // client keeps its own value for on a min channel, so nothing is withheld from a member.
        var forwarded = Channel(OtherChannelId);
        var message = ChannelMessage(
            fwdHeader: new MessageFwdHeader { FromId = new Peer(PeerType.Channel, OtherChannelId) });

        Reduce([message], chats: [forwarded]);

        forwarded.Min.ShouldBeTrue();
    }

    [Fact]
    public void A_reduced_channel_keeps_the_admin_rights_it_arrived_with()
    {
        // creator/admin_rights are protected by the min flag either way, so a caller reading a channel
        // they administer does not see their own rights blink.
        var forwarded = Channel(OtherChannelId);
        forwarded.Creator = true;
        forwarded.AdminRights = new TChatAdminRights();
        var message = ChannelMessage(
            fwdHeader: new MessageFwdHeader { FromId = new Peer(PeerType.Channel, OtherChannelId) });

        Reduce([message], chats: [forwarded]);

        forwarded.Min.ShouldBeTrue();
        forwarded.Creator.ShouldBeTrue();
        forwarded.AdminRights.ShouldNotBeNull();
    }

    [Fact]
    public void A_reduced_channel_drops_membership_and_rights_and_defers_stories_hidden()
    {
        var forwarded = Channel(OtherChannelId);
        forwarded.Left = true;
        forwarded.BannedRights = new TChatBannedRights();
        forwarded.ParticipantsCount = 4200;
        forwarded.SubscriptionUntilDate = 99;
        forwarded.StoriesHidden = true;
        var message = ChannelMessage(
            fwdHeader: new MessageFwdHeader { FromId = new Peer(PeerType.Channel, OtherChannelId) });

        Reduce([message], chats: [forwarded]);

        forwarded.Min.ShouldBeTrue();
        forwarded.Left.ShouldBeFalse();
        forwarded.BannedRights.ShouldBeNull();
        forwarded.ParticipantsCount.ShouldBeNull();
        forwarded.SubscriptionUntilDate.ShouldBeNull();
        // stories_hidden was not populated, so the client is told to keep its cached value rather
        // than read false as "not hidden".
        forwarded.StoriesHidden.ShouldBeFalse();
        forwarded.StoriesHiddenMin.ShouldBeTrue();
    }

    [Fact]
    public void A_reduced_channel_keeps_the_fields_a_min_constructor_does_carry()
    {
        var forwarded = Channel(OtherChannelId);
        forwarded.Title = "Ada's channel";
        forwarded.Username = "ada_channel";
        forwarded.Megagroup = true;
        forwarded.AccessHash = 4242;
        var message = ChannelMessage(
            fwdHeader: new MessageFwdHeader { FromId = new Peer(PeerType.Channel, OtherChannelId) });

        Reduce([message], chats: [forwarded]);

        forwarded.Title.ShouldBe("Ada's channel");
        forwarded.Username.ShouldBe("ada_channel");
        forwarded.Megagroup.ShouldBeTrue();
        forwarded.AccessHash.ShouldBe(4242);
    }

    [Fact]
    public void Every_reduced_peer_can_be_cited_back_through_the_resolver()
    {
        // The two directions have to agree: whatever is reduced here has to survive the resolver, or
        // the client is handed a peer it can never address.
        var message = ChannelMessage(
            fwdHeader: new MessageFwdHeader { FromId = new Peer(PeerType.Channel, OtherChannelId) });
        var sender = User(SenderUserId);
        var forwarded = Channel(OtherChannelId);

        Reduce([message], users: [sender], chats: [forwarded]);

        sender.Min.ShouldBeTrue();
        forwarded.Min.ShouldBeTrue();
        MessagePeerReferences.ReferencesUser(message, sender.Id).ShouldBeTrue();
        MessagePeerReferences.ReferencesChannel(message, forwarded.Id).ShouldBeTrue();
    }

    [Fact]
    public void A_bot_caller_gets_full_constructors()
    {
        // The resolver refuses fromMessage citations from bots outright, so a min peer would be one a
        // bot could never address.
        var sender = User(SenderUserId);

        Reduce([ChannelMessage()], users: [sender], callerUserId: BotCallerUserId);

        sender.Min.ShouldBeFalse();
    }

    // ---- Fixtures ------------------------------------------------------------------------------------

    private static void Reduce(
        IReadOnlyCollection<IMessageReadModel> messages,
        IReadOnlyCollection<ILayeredUser>? users = null,
        IReadOnlyCollection<IChat>? chats = null,
        long callerUserId = CallerUserId)
    {
        new MinConstructorReducer(new PeerHelper())
            .Reduce(Input(callerUserId), messages, users ?? [], chats ?? []);
    }

    private static TUser User(long userId, bool self = false) =>
        new() { Id = userId, Self = self };

    private static TChannel Channel(long channelId) =>
        new() { Id = channelId, Title = "channel", Photo = new TChatPhotoEmpty() };

    private static IRequestWithAccessHashKeyId Input(long userId)
    {
        var input = new Mock<IRequestInput>(MockBehavior.Loose);
        input.SetupGet(p => p.UserId).Returns(userId);

        return input.Object;
    }

    private static IMessageReadModel ChannelMessage(long senderUserId = SenderUserId,
        MessageFwdHeader? fwdHeader = null)
    {
        return Message(ContainerChannelId, PeerType.Channel, senderUserId, fwdHeader);
    }

    private static IMessageReadModel PrivateMessage(long senderUserId = SenderUserId)
    {
        return Message(CallerUserId, PeerType.User, senderUserId, fwdHeader: null);
    }

    private static IMessageReadModel Message(long ownerPeerId, PeerType toPeerType, long senderUserId,
        MessageFwdHeader? fwdHeader)
    {
        var message = new Mock<IMessageReadModel>(MockBehavior.Loose);
        message.SetupGet(p => p.OwnerPeerId).Returns(ownerPeerId);
        message.SetupGet(p => p.ToPeerId).Returns(ownerPeerId);
        message.SetupGet(p => p.ToPeerType).Returns(toPeerType);
        message.SetupGet(p => p.SenderUserId).Returns(senderUserId);
        message.SetupGet(p => p.SenderPeerId).Returns(senderUserId);
        message.SetupGet(p => p.FwdHeader).Returns(fwdHeader);

        return message.Object;
    }
}
