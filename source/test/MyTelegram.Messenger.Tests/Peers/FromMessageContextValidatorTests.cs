using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MyTelegram.Messenger.Services;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.Peers;

/// <summary>
/// Feature: the <c>input*FromMessage</c> constructors of
/// <a href="https://corefork.telegram.org/api/min">min constructors</a> are validated for every
/// request, not only for the handlers that remembered to ask.
///
/// <para>
/// Those constructors carry a cited message context instead of an access hash, so the access-hash
/// check has nothing to verify and passes them through. Whatever peer id the caller writes into one
/// then reaches the handler unchecked. The validator closes that by walking each request and proving
/// every cited context before the handler runs — including the ones buried in a wrapper, a reply
/// header or a vector.
/// </para>
/// </summary>
public class FromMessageContextValidatorTests
{
    private const long CallerUserId = 2_000_001;
    private const long TargetUserId = 2_000_002;
    private const long OtherUserId = 2_000_003;
    private const long ChannelId = 800_000_000_001;
    private const long ContainerChannelId = 800_000_000_009;
    private const int MsgId = 34;

    [Fact]
    public async Task A_channel_cited_by_message_context_is_validated()
    {
        var resolver = AcceptingResolver();
        var request = new Schema.Channels.RequestGetFullChannel
        {
            Channel = new TInputChannelFromMessage
            {
                Peer = Container(),
                MsgId = MsgId,
                ChannelId = ChannelId
            }
        };

        await Validator(resolver).ValidateAsync(Input(CallerUserId), request);

        resolver.Verify(
            p => p.ResolveChannelIdAsync(It.IsAny<IRequestInput>(), It.IsAny<IInputPeer>(), MsgId, ChannelId),
            Times.Once);
    }

    [Fact]
    public async Task A_user_cited_by_message_context_is_validated()
    {
        var resolver = AcceptingResolver();
        var request = new Schema.Messages.RequestGetHistory
        {
            Peer = new TInputPeerUserFromMessage
            {
                Peer = Container(),
                MsgId = MsgId,
                UserId = TargetUserId
            }
        };

        await Validator(resolver).ValidateAsync(Input(CallerUserId), request);

        resolver.Verify(
            p => p.ResolveUserIdAsync(It.IsAny<IRequestInput>(), It.IsAny<IInputPeer>(), MsgId, TargetUserId),
            Times.Once);
    }

    [Fact]
    public async Task A_context_wrapped_in_invokeWithLayer_is_still_validated()
    {
        var resolver = AcceptingResolver();
        var request = new RequestInvokeWithLayer
        {
            Layer = 222,
            Query = new Schema.Channels.RequestGetFullChannel
            {
                Channel = new TInputChannelFromMessage
                {
                    Peer = Container(),
                    MsgId = MsgId,
                    ChannelId = ChannelId
                }
            }
        };

        await Validator(resolver).ValidateAsync(Input(CallerUserId), request);

        resolver.Verify(
            p => p.ResolveChannelIdAsync(It.IsAny<IRequestInput>(), It.IsAny<IInputPeer>(), MsgId, ChannelId),
            Times.Once);
    }

    [Fact]
    public async Task A_context_nested_in_a_reply_header_is_still_validated()
    {
        var resolver = AcceptingResolver();
        var request = new Schema.Messages.RequestSendMessage
        {
            Peer = new TInputPeerSelf(),
            Message = "hi",
            ReplyTo = new TInputReplyToMessage
            {
                ReplyToMsgId = 7,
                ReplyToPeerId = new TInputPeerChannelFromMessage
                {
                    Peer = Container(),
                    MsgId = MsgId,
                    ChannelId = ChannelId
                }
            }
        };

        await Validator(resolver).ValidateAsync(Input(CallerUserId), request);

        resolver.Verify(
            p => p.ResolveChannelIdAsync(It.IsAny<IRequestInput>(), It.IsAny<IInputPeer>(), MsgId, ChannelId),
            Times.Once);
    }

    [Fact]
    public async Task Every_context_in_a_vector_is_validated_and_duplicates_are_proven_once()
    {
        var resolver = AcceptingResolver();
        var request = new Schema.Users.RequestGetUsers
        {
            Id =
            [
                new TInputUserFromMessage { Peer = Container(), MsgId = MsgId, UserId = TargetUserId },
                new TInputUserFromMessage { Peer = Container(), MsgId = MsgId, UserId = OtherUserId },
                new TInputUserFromMessage { Peer = Container(), MsgId = MsgId, UserId = TargetUserId },
                new TInputUserSelf()
            ]
        };

        await Validator(resolver).ValidateAsync(Input(CallerUserId), request);

        resolver.Verify(
            p => p.ResolveUserIdAsync(It.IsAny<IRequestInput>(), It.IsAny<IInputPeer>(), MsgId, TargetUserId),
            Times.Once);
        resolver.Verify(
            p => p.ResolveUserIdAsync(It.IsAny<IRequestInput>(), It.IsAny<IInputPeer>(), MsgId, OtherUserId),
            Times.Once);
    }

    [Fact]
    public async Task A_container_that_is_itself_a_min_context_is_validated_too()
    {
        var resolver = AcceptingResolver();
        var request = new Schema.Messages.RequestGetHistory
        {
            Peer = new TInputPeerUserFromMessage
            {
                Peer = new TInputPeerChannelFromMessage
                {
                    Peer = Container(),
                    MsgId = 9,
                    ChannelId = ChannelId
                },
                MsgId = MsgId,
                UserId = TargetUserId
            }
        };

        await Validator(resolver).ValidateAsync(Input(CallerUserId), request);

        resolver.Verify(
            p => p.ResolveUserIdAsync(It.IsAny<IRequestInput>(), It.IsAny<IInputPeer>(), MsgId, TargetUserId),
            Times.Once);
        resolver.Verify(
            p => p.ResolveChannelIdAsync(It.IsAny<IRequestInput>(), It.IsAny<IInputPeer>(), 9, ChannelId),
            Times.Once);
    }

    [Fact]
    public async Task A_request_without_any_min_context_costs_no_lookup()
    {
        var resolver = AcceptingResolver();
        var request = new Schema.Messages.RequestGetHistory
        {
            Peer = new TInputPeerChannel { ChannelId = ChannelId, AccessHash = 12345 }
        };

        await Validator(resolver).ValidateAsync(Input(CallerUserId), request);

        resolver.Verify(
            p => p.ResolveUserIdAsync(It.IsAny<IRequestInput>(), It.IsAny<IInputPeer>(), It.IsAny<int>(),
                It.IsAny<long>()), Times.Never);
        resolver.Verify(
            p => p.ResolveChannelIdAsync(It.IsAny<IRequestInput>(), It.IsAny<IInputPeer>(), It.IsAny<int>(),
                It.IsAny<long>()), Times.Never);
    }

    [Fact]
    public async Task A_context_that_does_not_hold_up_fails_the_whole_request()
    {
        var resolver = new Mock<IFromMessagePeerResolver>(MockBehavior.Loose);
        resolver
            .Setup(p => p.ResolveChannelIdAsync(It.IsAny<IRequestInput>(), It.IsAny<IInputPeer>(), It.IsAny<int>(),
                It.IsAny<long>()))
            .ThrowsAsync(new RpcException(RpcErrors.RpcErrors400.ChannelInvalid));

        var request = new Schema.Channels.RequestGetFullChannel
        {
            Channel = new TInputChannelFromMessage
            {
                Peer = Container(),
                MsgId = MsgId,
                ChannelId = ChannelId
            }
        };

        var exception = await Should.ThrowAsync<RpcException>(() =>
            Validator(resolver).ValidateAsync(Input(CallerUserId), request));

        exception.RpcError.Message.ShouldBe("CHANNEL_INVALID");
    }

    // ---- Fixtures ------------------------------------------------------------------------------------

    private static IInputPeer Container() =>
        new TInputPeerChannel { ChannelId = ContainerChannelId, AccessHash = 1 };

    private static IRequestInput Input(long userId)
    {
        var input = new Mock<IRequestInput>(MockBehavior.Loose);
        input.SetupGet(p => p.UserId).Returns(userId);

        return input.Object;
    }

    private static Mock<IFromMessagePeerResolver> AcceptingResolver()
    {
        var resolver = new Mock<IFromMessagePeerResolver>(MockBehavior.Loose);
        resolver
            .Setup(p => p.ResolveUserIdAsync(It.IsAny<IRequestInput>(), It.IsAny<IInputPeer>(), It.IsAny<int>(),
                It.IsAny<long>()))
            .ReturnsAsync((IRequestInput _, IInputPeer _, int _, long userId) => userId);
        resolver
            .Setup(p => p.ResolveChannelIdAsync(It.IsAny<IRequestInput>(), It.IsAny<IInputPeer>(), It.IsAny<int>(),
                It.IsAny<long>()))
            .ReturnsAsync((IRequestInput _, IInputPeer _, int _, long channelId) => channelId);

        return resolver;
    }

    private static FromMessageContextValidator Validator(Mock<IFromMessagePeerResolver> resolver) =>
        new(resolver.Object, NullLogger<FromMessageContextValidator>.Instance);
}
