using System.Reflection;
using EventFlow;
using EventFlow.Aggregates.ExecutionResults;
using EventFlow.Commands;
using EventFlow.Queries;
using Moq;
using MyTelegram.Domain.Aggregates.Dialog;
using MyTelegram.Messenger.Handlers.LatestLayer.Messages;
using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Schema.Messages;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.Drafts;

/// <summary>
/// Feature: <c>messages.clearAllDrafts</c>.
///
/// <para>It used to delete the draft rows on their own, which left <c>dialog.draft</c> in place — the
/// next <c>messages.getDialogs</c> handed every draft straight back — and told no other session
/// anything. Going through the dialog fixes both, and every draft of one dialog has to travel in one
/// command because a request command is deduplicated by the request's <c>msg_id</c> alone.</para>
/// </summary>
public class ClearAllDraftsHandlerTests
{
    private const long UserId = 2_000_001;
    private static readonly Peer ChannelPeer = new(PeerType.Channel, 1_555_001);
    private static readonly Peer UserPeer = new(PeerType.User, 3_000_002);
    private static readonly Peer MonoforumUser = new(PeerType.User, 4242);

    [Fact]
    public async Task Every_draft_of_every_dialog_is_cleared()
    {
        var fixture = new Fixture([
            DraftReadModel(UserPeer, Draft("in a private chat")),
            DraftReadModel(ChannelPeer, Draft("in a channel")),
            DraftReadModel(ChannelPeer, Draft("in a topic", topMsgId: 7)),
            DraftReadModel(ChannelPeer, Draft("in a monoforum topic", savedPeerId: MonoforumUser))
        ]);

        await fixture.InvokeAsync();

        // One per dialog, all of the dialog's topics inside it.
        fixture.Published.Count.ShouldBe(2);
        var channelCommand = fixture.Published
            .Single(p => p.Peer.Equals(ChannelPeer));
        channelCommand.RequestedTopics.Select(p => p.Key)
            .ShouldBe([DraftTopicKey.ChatLevel, "t7", $"m{MonoforumUser.PeerId}"], true);

        var userCommand = fixture.Published.Single(p => p.Peer.Equals(UserPeer));
        userCommand.OwnerPeerId.ShouldBe(UserId);
        userCommand.RequestedTopics.ShouldHaveSingleItem().IsChatLevel.ShouldBeTrue();
    }

    [Fact]
    public async Task Nothing_to_clear_is_not_an_error()
    {
        var fixture = new Fixture([]);

        var result = await fixture.InvokeAsync();

        result.ShouldBeOfType<TBoolTrue>();
        fixture.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_draft_row_with_no_peer_is_skipped()
    {
        // Rows written before the saved event carried its peer cannot be addressed at all.
        var fixture = new Fixture([DraftReadModel(null, Draft("orphan"))]);

        await fixture.InvokeAsync();

        fixture.Published.ShouldBeEmpty();
    }

    private static Draft Draft(string message, int? topMsgId = null, Peer? savedPeerId = null)
    {
        return new Draft(false, false, null, message, 0, topMsgId: topMsgId, savedPeerId: savedPeerId);
    }

    private static IDraftReadModel DraftReadModel(Peer? peer, Draft draft)
    {
        var readModel = new Mock<IDraftReadModel>(MockBehavior.Loose);
        readModel.SetupGet(p => p.OwnerPeerId).Returns(UserId);
        readModel.SetupGet(p => p.Peer).Returns(peer!);
        readModel.SetupGet(p => p.Draft).Returns(draft);

        return readModel.Object;
    }

    private sealed class Fixture
    {
        public List<ClearDraftsCommand> Published { get; } = [];

        private readonly ClearAllDraftsHandler _handler;

        public Fixture(IReadOnlyCollection<IDraftReadModel> drafts)
        {
            var queryProcessor = new Mock<IQueryProcessor>(MockBehavior.Loose);
            queryProcessor
                .Setup(p => p.ProcessAsync(It.IsAny<GetAllDraftQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(drafts);

            var commandBus = new Mock<ICommandBus>(MockBehavior.Loose);
            commandBus
                .Setup(p => p.PublishAsync(It.IsAny<ICommand<DialogAggregate, DialogId, IExecutionResult>>(),
                    It.IsAny<CancellationToken>()))
                .Callback((ICommand<DialogAggregate, DialogId, IExecutionResult> command, CancellationToken _) =>
                    Published.Add((ClearDraftsCommand)command))
                .ReturnsAsync(ExecutionResult.Success());

            _handler = new ClearAllDraftsHandler(queryProcessor.Object, commandBus.Object);
        }

        public async Task<IBool> InvokeAsync()
        {
            var input = new Mock<IRequestInput>(MockBehavior.Loose);
            input.SetupGet(p => p.UserId).Returns(UserId);

            var method = typeof(ClearAllDraftsHandler)
                .GetMethod("HandleCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

            try
            {
                return await (Task<IBool>)method.Invoke(_handler, [input.Object, new RequestClearAllDrafts()])!;
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }
    }
}
