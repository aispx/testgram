using System.Reflection;
using EventFlow;
using EventFlow.Aggregates.ExecutionResults;
using EventFlow.Commands;
using EventFlow.Queries;
using Moq;
using MyTelegram.Domain.Aggregates.Dialog;
using MyTelegram.Domain.Aggregates.Temp;
using MyTelegram.Messenger.Handlers.LatestLayer.Folders;
using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.Folders;

/// <summary>
/// Feature: <c>folders.editPeerFolders</c>, the archive.
///
/// <para>Only folders 0 and 1 exist ("no other folder_id is allowed at the moment"), and a peer with no dialog
/// row must not reach the saga: <c>DialogAggregate.UpdateDialogFolder</c> asserts the dialog exists, so the
/// command fails, <c>EditPeerFoldersSaga</c> waits forever for the event it counts, and the request is never
/// answered at all.</para>
/// See https://corefork.telegram.org/api/folders#peer-folders
/// </summary>
public class EditPeerFoldersHandlerTests
{
    private const long UserId = 2_000_001;
    private const long ChannelId = 1_555_001;
    private const long OtherUserId = 3_000_002;

    [Fact]
    public async Task A_peer_with_a_dialog_is_moved_to_the_archive()
    {
        var fixture = new Fixture(peersWithDialog: [ChannelId]);

        var result = await fixture.InvokeAsync([Archive(Channel())]);

        // The updates answer is produced once the saga has the pts.
        result.ShouldBeNull();
        var command = fixture.Published.ShouldHaveSingleItem();
        command.FolderPeers.ShouldHaveSingleItem()
            .ShouldBeOfType<TInputFolderPeer>().FolderId.ShouldBe(1);
    }

    [Fact]
    public async Task A_folder_other_than_the_archive_or_the_main_list_is_refused()
    {
        var fixture = new Fixture(peersWithDialog: [ChannelId]);

        (await Should.ThrowAsync<RpcException>(() =>
                fixture.InvokeAsync([new TInputFolderPeer { Peer = Channel(), FolderId = 2 }])))
            .RpcError.Message.ShouldBe("FOLDER_ID_INVALID");

        fixture.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task Peers_without_a_dialog_are_dropped_and_the_rest_still_travels()
    {
        var fixture = new Fixture(peersWithDialog: [ChannelId]);

        await fixture.InvokeAsync([Archive(Channel()), Archive(User())]);

        var command = fixture.Published.ShouldHaveSingleItem();
        command.FolderPeers.ShouldHaveSingleItem()
            .ShouldBeOfType<TInputFolderPeer>().Peer.ShouldBeOfType<TInputPeerChannel>();
    }

    [Fact]
    public async Task Nothing_that_has_a_dialog_is_an_invalid_peer()
    {
        var fixture = new Fixture(peersWithDialog: []);

        (await Should.ThrowAsync<RpcException>(() => fixture.InvokeAsync([Archive(Channel())])))
            .RpcError.Message.ShouldBe("PEER_ID_INVALID");
    }

    [Fact]
    public async Task An_empty_request_is_refused()
    {
        var fixture = new Fixture(peersWithDialog: [ChannelId]);

        (await Should.ThrowAsync<RpcException>(() => fixture.InvokeAsync([])))
            .RpcError.Message.ShouldBe("PEER_ID_INVALID");
    }

    private static TInputFolderPeer Archive(IInputPeer peer) => new() { Peer = peer, FolderId = 1 };

    private static IInputPeer Channel() => new TInputPeerChannel { ChannelId = ChannelId, AccessHash = 1 };

    private static IInputPeer User() => new TInputPeerUser { UserId = OtherUserId, AccessHash = 2 };

    private sealed class Fixture
    {
        public List<StartEditPeerFoldersCommand> Published { get; } = [];

        private readonly EditPeerFoldersHandler _handler;

        public Fixture(IReadOnlyCollection<long> peersWithDialog)
        {
            var queryProcessor = new Mock<IQueryProcessor>(MockBehavior.Loose);
            queryProcessor
                .Setup(p => p.ProcessAsync(It.IsAny<GetDialogByIdQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((GetDialogByIdQuery query, CancellationToken _) =>
                {
                    var found = peersWithDialog.Any(peerId =>
                        query.Id == DialogId.Create(UserId, PeerType.Channel, peerId).Value ||
                        query.Id == DialogId.Create(UserId, PeerType.User, peerId).Value);

                    if (!found)
                    {
                        return null;
                    }

                    var dialog = new Mock<IDialogReadModel>(MockBehavior.Loose);
                    dialog.SetupGet(p => p.IsDeleted).Returns(false);

                    return dialog.Object;
                });

            var peerHelper = new Mock<IPeerHelper>(MockBehavior.Loose);
            peerHelper.Setup(p => p.GetPeer(It.IsAny<IInputPeer>(), It.IsAny<long>()))
                .Returns((IInputPeer peer, long selfUserId) => peer switch
                {
                    TInputPeerChannel channel => new Peer(PeerType.Channel, channel.ChannelId),
                    TInputPeerUser user => new Peer(PeerType.User, user.UserId),
                    _ => new Peer(PeerType.User, selfUserId)
                });

            var commandBus = new Mock<ICommandBus>(MockBehavior.Loose);
            commandBus
                .Setup(p => p.PublishAsync(It.IsAny<ICommand<TempAggregate, TempId, IExecutionResult>>(),
                    It.IsAny<CancellationToken>()))
                .Callback((ICommand<TempAggregate, TempId, IExecutionResult> command, CancellationToken _) =>
                    Published.Add((StartEditPeerFoldersCommand)command))
                .ReturnsAsync(ExecutionResult.Success());

            _handler = new EditPeerFoldersHandler(commandBus.Object, peerHelper.Object, queryProcessor.Object);
        }

        public async Task<IUpdates> InvokeAsync(List<IInputFolderPeer> folderPeers)
        {
            var input = new Mock<IRequestInput>(MockBehavior.Loose);
            input.SetupGet(p => p.UserId).Returns(UserId);

            var request = new MyTelegram.Schema.Folders.RequestEditPeerFolders
            {
                FolderPeers = new TVector<IInputFolderPeer>(folderPeers)
            };

            var method = typeof(EditPeerFoldersHandler)
                .GetMethod("HandleCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

            try
            {
                return await (Task<IUpdates>)method.Invoke(_handler, [input.Object, request])!;
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }
    }
}
