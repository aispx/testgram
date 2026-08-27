using System.Reflection;
using EventFlow.Queries;
using Moq;
using MyTelegram.Converters.Responses;
using MyTelegram.Converters.TLObjects.Interfaces;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Handlers.LatestLayer.Messages;
using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Schema.Messages;
using MyTelegram.Services.Services;
using MyTelegram.Services.TLObjectConverters;

namespace MyTelegram.Messenger.Tests.Drafts;

/// <summary>
/// Feature: <c>messages.getAllDrafts</c>, which answers with the latest <c>updateDraftMessage</c> for
/// every chat that holds a draft (https://corefork.telegram.org/api/drafts).
///
/// <para>The peers have to travel with the updates: TDLib feeds this answer straight into its update
/// manager and repairs a draft for a dialog it does not know with <c>messages.getPeerDialogs</c>, but
/// only when it has read access to the peer — that is, only when the user or the channel came with the
/// answer. It used to send empty <c>users</c> and <c>chats</c>.</para>
/// </summary>
public class GetAllDraftsHandlerTests
{
    private const long UserId = 2_000_001;
    private static readonly Peer UserPeer = new(PeerType.User, 3_000_002);
    private static readonly Peer ChannelPeer = new(PeerType.Channel, 1_555_001);
    private static readonly Peer MonoforumUser = new(PeerType.User, 4242);

    [Fact]
    public async Task Every_draft_comes_back_as_an_update_naming_its_peer_and_topic()
    {
        var result = await InvokeAsync([
            DraftReadModel(UserPeer, Draft("in a private chat")),
            DraftReadModel(ChannelPeer, Draft("in a topic", topMsgId: 7)),
            DraftReadModel(ChannelPeer, Draft("in a monoforum topic", savedPeerId: MonoforumUser))
        ]);

        var updates = result.ShouldBeOfType<TUpdates>().Updates.Cast<TUpdateDraftMessage>().ToList();
        updates.Count.ShouldBe(3);
        updates[0].Peer.ShouldBeOfType<TPeerUser>().UserId.ShouldBe(UserPeer.PeerId);
        updates[0].TopMsgId.ShouldBeNull();
        updates[1].TopMsgId.ShouldBe(7);
        updates[2].SavedPeerId.ShouldBeOfType<TPeerUser>().UserId.ShouldBe(MonoforumUser.PeerId);
    }

    [Fact]
    public async Task The_peers_of_the_drafts_come_with_the_answer()
    {
        var result = await InvokeAsync([
            DraftReadModel(UserPeer, Draft("in a private chat")),
            DraftReadModel(ChannelPeer, Draft("in a channel"))
        ]);

        var updates = result.ShouldBeOfType<TUpdates>();
        updates.Users.ShouldHaveSingleItem().Id.ShouldBe(UserPeer.PeerId);
        updates.Chats.ShouldHaveSingleItem().Id.ShouldBe(ChannelPeer.PeerId);
    }

    [Fact]
    public async Task A_peer_is_named_once_however_many_drafts_it_holds()
    {
        var result = await InvokeAsync([
            DraftReadModel(ChannelPeer, Draft("in a channel")),
            DraftReadModel(ChannelPeer, Draft("in a topic", topMsgId: 7))
        ]);

        result.ShouldBeOfType<TUpdates>().Chats.Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_saved_messages_chat_names_its_user_too()
    {
        // A draft in Saved Messages has PeerType.Self rather than PeerType.User, and it is still a user.
        var result = await InvokeAsync([DraftReadModel(new Peer(PeerType.Self, UserId), Draft("note to self"))]);

        result.ShouldBeOfType<TUpdates>().Users.ShouldHaveSingleItem().Id.ShouldBe(UserId);
    }

    [Fact]
    public async Task A_draft_row_with_no_peer_is_left_out()
    {
        // Rows written before the saved event carried its peer: an update about them names nothing.
        var result = await InvokeAsync([DraftReadModel(null, Draft("orphan"))]);

        result.ShouldBeOfType<TUpdates>().Updates.ShouldBeEmpty();
    }

    private static Draft Draft(string message, int? topMsgId = null, Peer? savedPeerId = null)
    {
        return new Draft(false, false, null, message, 1_700_000_000, topMsgId: topMsgId, savedPeerId: savedPeerId);
    }

    private static IDraftReadModel DraftReadModel(Peer? peer, Draft draft)
    {
        var readModel = new Mock<IDraftReadModel>(MockBehavior.Loose);
        readModel.SetupGet(p => p.OwnerPeerId).Returns(UserId);
        readModel.SetupGet(p => p.Peer).Returns(peer!);
        readModel.SetupGet(p => p.Draft).Returns(draft);

        return readModel.Object;
    }

    private static async Task<IUpdates> InvokeAsync(IReadOnlyCollection<IDraftReadModel> drafts)
    {
        var queryProcessor = new Mock<IQueryProcessor>(MockBehavior.Loose);
        queryProcessor
            .Setup(p => p.ProcessAsync(It.IsAny<GetAllDraftQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(drafts);

        var draftMessageConverter = new Mock<IDraftMessageConverter>(MockBehavior.Loose);
        draftMessageConverter
            .Setup(p => p.ToDraftMessage(It.IsAny<IDraftReadModel>()))
            .Returns((IDraftReadModel readModel) => new TDraftMessage
            {
                Message = readModel.Draft.Message,
                Date = readModel.Draft.Date,
                Entities = new TVector<IMessageEntity>()
            });

        var layeredService = new Mock<ILayeredService<IDraftMessageConverter>>(MockBehavior.Loose);
        layeredService.Setup(p => p.GetConverter(It.IsAny<int>())).Returns(draftMessageConverter.Object);

        var userConverterService = new Mock<IUserConverterService>(MockBehavior.Loose);
        userConverterService
            .Setup(p => p.GetUserListAsync(It.IsAny<IRequestWithAccessHashKeyId>(), It.IsAny<List<long>>(),
                It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<int>()))
            .ReturnsAsync((IRequestWithAccessHashKeyId _, List<long> userIds, bool _, bool _, int _) =>
                [.. userIds.Select(ILayeredUser (id) => new TUser { Id = id })]);

        var chatConverterService = new Mock<IChatConverterService>(MockBehavior.Loose);
        chatConverterService
            .Setup(p => p.GetChannelListAsync(It.IsAny<IRequestWithAccessHashKeyId>(), It.IsAny<List<long>>(),
                It.IsAny<IReadOnlyCollection<IChannelMemberReadModel>?>(), It.IsAny<int>()))
            .ReturnsAsync((IRequestWithAccessHashKeyId _, List<long> channelIds,
                    IReadOnlyCollection<IChannelMemberReadModel>? _, int _) =>
                [.. channelIds.Select(IChat (id) => new TChannel { Id = id, Title = string.Empty })]);

        var updatesConverterService = new UpdatesConverterService(
            new Mock<IMessageConverterService>(MockBehavior.Loose).Object,
            chatConverterService.Object,
            new Mock<IMessageResponseService>(MockBehavior.Loose).Object,
            layeredService.Object);

        var handler = new GetAllDraftsHandler(queryProcessor.Object, updatesConverterService,
            userConverterService.Object, chatConverterService.Object);

        var input = new Mock<IRequestInput>(MockBehavior.Loose);
        input.SetupGet(p => p.UserId).Returns(UserId);
        input.SetupGet(p => p.Layer).Returns(Layers.LayerLatest);

        var method = typeof(GetAllDraftsHandler)
            .GetMethod("HandleCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        try
        {
            return await (Task<IUpdates>)method.Invoke(handler, [input.Object, new RequestGetAllDrafts()])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }
}
