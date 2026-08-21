using EventFlow.Queries;
using Moq;
using MyTelegram.Messenger.Services;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.Peers;

/// <summary>
/// Feature: the <c>input*FromMessage</c> constructors of the
/// <a href="https://corefork.telegram.org/api/peers">peer database</a>.
///
/// <para>
/// A peer that was only ever seen through a <a href="https://corefork.telegram.org/api/min">min
/// constructor</a> has no usable access hash, so the client names it by the context it appeared in:
/// a container peer plus a message id. Every field of that citation is attacker-chosen, so the
/// server has to prove the cited message is one the caller can read and that the peer really shows
/// up in it — otherwise the constructor is simply "give me any peer id I ask for".
/// </para>
/// </summary>
public class FromMessagePeerResolverTests
{
    private const long CallerUserId = 2_000_001;
    private const long SenderUserId = 2_000_002;
    private const long StrangerUserId = 2_000_003;
    private const long BotUserId = 600_000_000_010;
    private const long ChannelId = 800_000_000_001;
    private const long OtherChannelId = 800_000_000_002;
    private const int MsgId = 34;

    [Fact]
    public async Task The_sender_of_the_cited_message_resolves()
    {
        var resolver = CreateResolver(MessageFrom(SenderUserId));

        var resolved = await resolver.ResolveUserIdAsync(Input(CallerUserId), Channel(), MsgId, SenderUserId);

        resolved.ShouldBe(SenderUserId);
    }

    [Fact]
    public async Task A_user_only_mentioned_by_name_resolves()
    {
        var message = MessageFrom(SenderUserId, mentioned: [StrangerUserId]);
        var resolver = CreateResolver(message);

        var resolved = await resolver.ResolveUserIdAsync(Input(CallerUserId), Channel(), MsgId, StrangerUserId);

        resolved.ShouldBe(StrangerUserId);
    }

    [Fact]
    public async Task The_original_sender_in_the_forward_header_resolves()
    {
        var message = MessageFrom(SenderUserId,
            fwdHeader: new MessageFwdHeader { FromId = new Peer(PeerType.User, StrangerUserId) });
        var resolver = CreateResolver(message);

        var resolved = await resolver.ResolveUserIdAsync(Input(CallerUserId), Channel(), MsgId, StrangerUserId);

        resolved.ShouldBe(StrangerUserId);
    }

    [Fact]
    public async Task A_user_who_does_not_appear_in_the_cited_message_is_PEER_ID_INVALID()
    {
        var resolver = CreateResolver(MessageFrom(SenderUserId));

        var exception = await Should.ThrowAsync<RpcException>(() =>
            resolver.ResolveUserIdAsync(Input(CallerUserId), Channel(), MsgId, StrangerUserId));

        exception.RpcError.Message.ShouldBe("PEER_ID_INVALID");
    }

    [Fact]
    public async Task A_message_the_caller_cannot_read_is_MSG_ID_INVALID()
    {
        var resolver = CreateResolver(message: null);

        var exception = await Should.ThrowAsync<RpcException>(() =>
            resolver.ResolveUserIdAsync(Input(CallerUserId), Channel(), MsgId, SenderUserId));

        exception.RpcError.Message.ShouldBe("MSG_ID_INVALID");
    }

    [Fact]
    public async Task A_zero_message_id_is_MSG_ID_INVALID()
    {
        var resolver = CreateResolver(MessageFrom(SenderUserId));

        var exception = await Should.ThrowAsync<RpcException>(() =>
            resolver.ResolveUserIdAsync(Input(CallerUserId), Channel(), msgId: 0, SenderUserId));

        exception.RpcError.Message.ShouldBe("MSG_ID_INVALID");
    }

    [Fact]
    public async Task Bots_cannot_use_fromMessage_constructors()
    {
        var resolver = CreateResolver(MessageFrom(SenderUserId));

        var exception = await Should.ThrowAsync<RpcException>(() =>
            resolver.ResolveUserIdAsync(Input(BotUserId), Channel(), MsgId, SenderUserId));

        exception.RpcError.Message.ShouldBe("FROM_MESSAGE_BOT_DISABLED");
    }

    [Fact]
    public async Task A_channel_the_caller_cannot_read_stops_the_resolution()
    {
        var resolver = CreateResolver(MessageFrom(SenderUserId), callerHasChannelReadAccess: false);

        var exception = await Should.ThrowAsync<RpcException>(() =>
            resolver.ResolveUserIdAsync(Input(CallerUserId), Channel(), MsgId, SenderUserId));

        exception.RpcError.Message.ShouldBe("CHANNEL_PRIVATE");
    }

    [Fact]
    public async Task The_chat_the_cited_message_lives_in_resolves_as_a_channel()
    {
        var resolver = CreateResolver(MessageFrom(SenderUserId));

        var resolved = await resolver.ResolveChannelIdAsync(Input(CallerUserId), Channel(), MsgId, ChannelId);

        resolved.ShouldBe(ChannelId);
    }

    [Fact]
    public async Task A_channel_that_does_not_appear_in_the_cited_message_is_CHANNEL_INVALID()
    {
        var resolver = CreateResolver(MessageFrom(SenderUserId));

        var exception = await Should.ThrowAsync<RpcException>(() =>
            resolver.ResolveChannelIdAsync(Input(CallerUserId), Channel(), MsgId, OtherChannelId));

        exception.RpcError.Message.ShouldBe("CHANNEL_INVALID");
    }

    [Fact]
    public async Task A_basic_group_container_is_PEER_ID_INVALID()
    {
        // Testgram keeps no basic groups, so no message can ever live under a chat id.
        var resolver = CreateResolver(MessageFrom(SenderUserId));

        var exception = await Should.ThrowAsync<RpcException>(() =>
            resolver.ResolveUserIdAsync(Input(CallerUserId), new TInputPeerChat { ChatId = 700_000_000_001 },
                MsgId, SenderUserId));

        exception.RpcError.Message.ShouldBe("PEER_ID_INVALID");
    }

    // ---- Fixtures ------------------------------------------------------------------------------------

    private static IInputPeer Channel() => new TInputPeerChannel { ChannelId = ChannelId, AccessHash = 1 };

    private static IRequestInput Input(long userId)
    {
        var input = new Mock<IRequestInput>(MockBehavior.Loose);
        input.SetupGet(p => p.UserId).Returns(userId);

        return input.Object;
    }

    private static IMessageReadModel MessageFrom(long senderUserId,
        List<long>? mentioned = null,
        MessageFwdHeader? fwdHeader = null)
    {
        var message = new Mock<IMessageReadModel>(MockBehavior.Loose);
        message.SetupGet(p => p.OwnerPeerId).Returns(ChannelId);
        message.SetupGet(p => p.ToPeerId).Returns(ChannelId);
        message.SetupGet(p => p.ToPeerType).Returns(PeerType.Channel);
        message.SetupGet(p => p.SenderUserId).Returns(senderUserId);
        message.SetupGet(p => p.SenderPeerId).Returns(senderUserId);
        message.SetupGet(p => p.MentionedUserIds).Returns(mentioned);
        message.SetupGet(p => p.FwdHeader).Returns(fwdHeader);

        return message.Object;
    }

    private static FromMessagePeerResolver CreateResolver(IMessageReadModel? message,
        bool callerHasChannelReadAccess = true)
    {
        var queryProcessor = new Mock<IQueryProcessor>(MockBehavior.Loose);
        queryProcessor
            .Setup(p => p.ProcessAsync(It.IsAny<GetMessageByPeerIdAndMessageIdQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(message);

        var channelReadModel = new Mock<IChannelReadModel>(MockBehavior.Loose);
        channelReadModel.SetupGet(p => p.ChannelId).Returns(ChannelId);
        channelReadModel.SetupGet(p => p.IsDeleted).Returns(false);

        var channelAppService = new Mock<IChannelAppService>(MockBehavior.Loose);
        channelAppService.Setup(p => p.GetAsync(It.IsAny<long?>())).ReturnsAsync(channelReadModel.Object);
        channelAppService
            .Setup(p => p.SendRpcErrorIfNoReadAccessAsync(It.IsAny<IRequestInput>(), It.IsAny<IChannelReadModel>()))
            .Returns(() => callerHasChannelReadAccess
                ? Task.FromResult(false)
                : throw new RpcException(RpcErrors.RpcErrors400.ChannelPrivate));

        return new FromMessagePeerResolver(queryProcessor.Object, new PeerHelper(), channelAppService.Object);
    }
}
