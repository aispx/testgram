using System.Reflection;
using EventFlow;
using EventFlow.Aggregates.ExecutionResults;
using EventFlow.Commands;
using Moq;
using MyTelegram.Domain.Aggregates.Dialog;
using MyTelegram.Messenger.Handlers.LatestLayer.Messages;
using MyTelegram.Messenger.Services.Entities;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Schema.Messages;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.Drafts;

/// <summary>
/// Feature: <c>messages.saveDraft</c>, the writing half of
/// <a href="https://corefork.telegram.org/api/drafts">drafts</a>.
///
/// <para>What matters here: an empty request is a clear rather than an empty draft (that is how every
/// client drops a cloud draft), the draft is keyed by the topic named in <c>reply_to</c> rather than by
/// the chat alone, a <c>reply_to</c> that only carries <c>top_msg_id</c> is not a reply, and the owner
/// and peer travel with the command so a draft in a chat with no dialog is not lost.</para>
/// </summary>
public class SaveDraftHandlerTests
{
    private const long UserId = 2_000_001;
    private const long ChannelId = 1_555_001;
    private static readonly Peer ChannelPeer = new(PeerType.Channel, ChannelId);

    [Fact]
    public async Task A_draft_with_text_is_saved_with_the_owner_and_peer_of_the_request()
    {
        var fixture = new Fixture();

        await fixture.InvokeAsync(Request("hello"));

        var command = fixture.Published.OfType<SaveDraftCommand>().ShouldHaveSingleItem();
        command.OwnerPeerId.ShouldBe(UserId);
        command.Peer.ShouldBe(ChannelPeer);
        command.Draft.Message.ShouldBe("hello");
        command.Draft.TopMsgId.ShouldBeNull();
        command.Draft.SavedPeerId.ShouldBeNull();
    }

    [Fact]
    public async Task An_empty_request_clears_the_draft_instead_of_storing_an_empty_one()
    {
        var fixture = new Fixture();

        await fixture.InvokeAsync(Request(string.Empty));

        fixture.Published.OfType<SaveDraftCommand>().ShouldBeEmpty();
        var command = fixture.Published.OfType<ClearDraftsCommand>().ShouldHaveSingleItem();
        command.RequestedTopics.ShouldHaveSingleItem().IsChatLevel.ShouldBeTrue();
    }

    [Fact]
    public async Task A_reply_with_no_text_is_still_a_draft()
    {
        var fixture = new Fixture();

        await fixture.InvokeAsync(Request(string.Empty,
            new TInputReplyToMessage { ReplyToMsgId = 42 }));

        var command = fixture.Published.OfType<SaveDraftCommand>().ShouldHaveSingleItem();
        command.Draft.ReplyToMsgId.ShouldBe(42);
    }

    [Fact]
    public async Task A_reply_to_that_only_names_a_topic_is_a_clear_of_that_topic()
    {
        var fixture = new Fixture();

        // How TDLib clears the draft of a topic: an empty message and an inputReplyToMessage carrying
        // nothing but top_msg_id (SaveDraftMessageQuery). Treating that as a reply would store a draft
        // that can never be cleared again.
        await fixture.InvokeAsync(Request(string.Empty,
            new TInputReplyToMessage { ReplyToMsgId = 0, TopMsgId = 7 }));

        fixture.Published.OfType<SaveDraftCommand>().ShouldBeEmpty();
        var command = fixture.Published.OfType<ClearDraftsCommand>().ShouldHaveSingleItem();
        command.RequestedTopics.ShouldHaveSingleItem().TopMsgId.ShouldBe(7);
    }

    [Fact]
    public async Task A_draft_in_a_forum_topic_is_keyed_by_the_topic()
    {
        var fixture = new Fixture();

        await fixture.InvokeAsync(Request("in the topic",
            new TInputReplyToMessage { ReplyToMsgId = 100, TopMsgId = 7 }));

        var command = fixture.Published.OfType<SaveDraftCommand>().ShouldHaveSingleItem();
        command.Draft.TopMsgId.ShouldBe(7);
        command.Draft.ReplyToMsgId.ShouldBe(100);
    }

    [Fact]
    public async Task A_draft_in_a_monoforum_topic_is_keyed_by_the_saved_peer()
    {
        var fixture = new Fixture();

        await fixture.InvokeAsync(Request("in the monoforum topic",
            new TInputReplyToMonoForum { MonoforumPeerId = new TInputPeerUser { UserId = 4242 } }));

        var command = fixture.Published.OfType<SaveDraftCommand>().ShouldHaveSingleItem();
        command.Draft.SavedPeerId!.PeerId.ShouldBe(4242);
        command.Draft.TopMsgId.ShouldBeNull();
    }

    [Fact]
    public async Task A_dice_can_not_be_put_in_a_draft()
    {
        var fixture = new Fixture();

        // The value is minted at send time, so a draft would carry a stale roll.
        var exception = await Should.ThrowAsync<Exception>(() =>
            fixture.InvokeAsync(Request("roll", media: new TInputMediaDice { Emoticon = "🎲" })));

        exception.Message.ShouldContain("MEDIA_INVALID");
        fixture.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_media_is_stored_as_the_input_media_the_client_sent()
    {
        var fixture = new Fixture();
        var media = new TInputMediaWebPage { Url = "https://telegram.org" };

        await fixture.InvokeAsync(Request("look at this", media: media));

        var command = fixture.Published.OfType<SaveDraftCommand>().ShouldHaveSingleItem();
        // draftMessage.media is an InputMedia: it is echoed back, never uploaded.
        command.Draft.Media2.ShouldBeSameAs(media);
        command.Draft.Media.ShouldBeNull();
    }

    private static RequestSaveDraft Request(string message,
        IInputReplyTo? replyTo = null,
        IInputMedia? media = null)
    {
        return new RequestSaveDraft
        {
            Peer = new TInputPeerChannel { ChannelId = ChannelId, AccessHash = 0 },
            Message = message,
            ReplyTo = replyTo,
            Media = media
        };
    }

    private sealed class Fixture
    {
        public List<object> Published { get; } = [];

        private readonly SaveDraftHandler _handler;

        public Fixture()
        {
            var commandBus = new Mock<ICommandBus>(MockBehavior.Loose);
            commandBus
                .Setup(p => p.PublishAsync(It.IsAny<ICommand<DialogAggregate, DialogId, IExecutionResult>>(),
                    It.IsAny<CancellationToken>()))
                .Callback((ICommand<DialogAggregate, DialogId, IExecutionResult> command, CancellationToken _) =>
                    Published.Add(command))
                .ReturnsAsync(ExecutionResult.Success());

            var peerHelper = new Mock<IPeerHelper>(MockBehavior.Loose);
            peerHelper
                .Setup(p => p.GetPeer(It.IsAny<IInputPeer?>(), It.IsAny<long>()))
                .Returns((IInputPeer? peer, long selfUserId) => peer switch
                {
                    null => null,
                    TInputPeerChannel channel => new Peer(PeerType.Channel, channel.ChannelId),
                    TInputPeerUser user => new Peer(PeerType.User, user.UserId),
                    _ => new Peer(PeerType.User, selfUserId)
                });

            var effectService = new Mock<IMessageEffectAppService>(MockBehavior.Loose);
            effectService
                .Setup(p => p.ValidateEffectAsync(It.IsAny<long?>(), It.IsAny<long>(), It.IsAny<PeerType>()))
                .ReturnsAsync((long? effectId, long _, PeerType _) => effectId);

            var entityService = new Mock<IMessageEntityService>(MockBehavior.Loose);
            entityService
                .Setup(p => p.ProcessAsync(It.IsAny<string?>(), It.IsAny<IEnumerable<IMessageEntity>?>(),
                    It.IsAny<Peer?>(), It.IsAny<MessageEntityProcessingOptions?>()))
                .ReturnsAsync(MessageEntityProcessingResult.Empty);

            _handler = new SaveDraftHandler(commandBus.Object, peerHelper.Object,
                effectService.Object, entityService.Object);
        }

        public Task InvokeAsync(RequestSaveDraft request)
        {
            var input = new Mock<IRequestInput>(MockBehavior.Loose);
            input.SetupGet(p => p.UserId).Returns(UserId);
            input.SetupGet(p => p.Layer).Returns(Layers.LayerLatest);

            var method = typeof(SaveDraftHandler)
                .GetMethod("HandleCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

            try
            {
                return (Task)method.Invoke(_handler, [input.Object, request])!;
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }
    }
}
