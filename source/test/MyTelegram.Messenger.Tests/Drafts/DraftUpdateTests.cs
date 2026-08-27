using EventFlow;
using EventFlow.Aggregates;
using Moq;
using MyTelegram.Converters.TLObjects.Interfaces;
using MyTelegram.Core;
using MyTelegram.Domain.Aggregates.Dialog;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.QueryServer.DomainEventHandlers;
using MyTelegram.Messenger.Services.Caching;
using MyTelegram.Schema;
using MyTelegram.Services.Services;
using MyTelegram.Services.TLObjectConverters;

namespace MyTelegram.Messenger.Tests.Drafts;

/// <summary>
/// Feature: "New drafts are automatically sent to all devices via updateDraftMessage updates"
/// (https://corefork.telegram.org/api/drafts).
///
/// <para>Nothing used to push those updates, so a second device never learned about a draft — Android
/// asks for the whole list once per account and never again, and TDLib clears nothing but secret chat
/// drafts locally and waits for <c>draftMessageEmpty</c> from the server. The peer travels with the
/// update because TDLib repairs a draft for an unknown dialog only when it has read access to it, and
/// the session that made the change is left out because it already applied the draft locally.</para>
/// </summary>
public class DraftUpdateTests
{
    private const long OwnerUserId = 2_000_001;
    private const long AuthKeyId = 777;
    private static readonly Peer UserPeer = new(PeerType.User, 3_000_002);
    private static readonly Peer MonoforumUser = new(PeerType.User, 4242);

    [Fact]
    public async Task A_saved_draft_is_pushed_to_the_other_sessions()
    {
        var fixture = new Fixture();

        await fixture.HandleSavedAsync(Draft("hello"));

        var update = fixture.SingleUpdate();
        update.Peer.ShouldBeOfType<TPeerUser>().UserId.ShouldBe(UserPeer.PeerId);
        update.TopMsgId.ShouldBeNull();
        update.SavedPeerId.ShouldBeNull();
        update.Draft.ShouldBeOfType<TDraftMessage>().Message.ShouldBe("hello");
        fixture.ExcludedAuthKeyId.ShouldBe(AuthKeyId);
        fixture.PushedToPeer.ShouldBe(new Peer(PeerType.User, OwnerUserId));
    }

    [Fact]
    public async Task The_peer_of_the_draft_travels_with_the_update()
    {
        var fixture = new Fixture();

        await fixture.HandleSavedAsync(Draft("hello"));

        fixture.PushedUpdates!.Users.ShouldHaveSingleItem().Id.ShouldBe(UserPeer.PeerId);
    }

    [Fact]
    public async Task The_saved_messages_chat_names_its_user_too()
    {
        var fixture = new Fixture();

        // A draft in Saved Messages has PeerType.Self rather than PeerType.User, and it is still a user.
        await fixture.HandleSavedAsync(Draft("note to self"), new Peer(PeerType.Self, OwnerUserId));

        fixture.PushedUpdates!.Users.ShouldHaveSingleItem().Id.ShouldBe(OwnerUserId);
    }

    [Fact]
    public async Task A_draft_in_a_topic_names_the_topic()
    {
        var fixture = new Fixture();

        await fixture.HandleSavedAsync(Draft("hello", topMsgId: 7));

        fixture.SingleUpdate().TopMsgId.ShouldBe(7);
    }

    [Fact]
    public async Task A_draft_in_a_monoforum_topic_names_the_saved_peer()
    {
        var fixture = new Fixture();

        await fixture.HandleSavedAsync(Draft("hello", savedPeerId: MonoforumUser));

        fixture.SingleUpdate().SavedPeerId.ShouldBeOfType<TPeerUser>().UserId.ShouldBe(MonoforumUser.PeerId);
    }

    [Fact]
    public async Task A_cleared_draft_is_pushed_as_draftMessageEmpty()
    {
        var fixture = new Fixture();

        await fixture.HandleClearedAsync([DraftTopic.ChatLevel]);

        var update = fixture.SingleUpdate();
        update.Draft.ShouldBeOfType<TDraftMessageEmpty>();
        update.TopMsgId.ShouldBeNull();
    }

    [Fact]
    public async Task Clearing_several_topics_pushes_one_update_each()
    {
        var fixture = new Fixture();

        await fixture.HandleClearedAsync([DraftTopic.ChatLevel, new DraftTopic(7), new DraftTopic(null, MonoforumUser)]);

        var updates = fixture.PushedUpdates!.Updates.Cast<TUpdateDraftMessage>().ToList();
        updates.Count.ShouldBe(3);
        updates.ShouldAllBe(p => p.Draft is TDraftMessageEmpty);
        updates.Select(p => p.TopMsgId).ShouldBe([null, 7, null]);
    }

    [Fact]
    public async Task A_clear_written_before_the_event_carried_its_peer_is_dropped()
    {
        var fixture = new Fixture();

        // Events already in the store have no peer at all: there is nothing to address an update to.
        await fixture.HandleClearedAsync([DraftTopic.ChatLevel], withoutPeer: true);

        fixture.PushedUpdates.ShouldBeNull();
    }

    private static Draft Draft(string message, int? topMsgId = null, Peer? savedPeerId = null)
    {
        return new Draft(false, false, null, message, 1_700_000_000, topMsgId: topMsgId, savedPeerId: savedPeerId);
    }

    private sealed class Fixture
    {
        private readonly DraftDomainEventHandler _handler;

        public TUpdates? PushedUpdates { get; private set; }
        public Peer? PushedToPeer { get; private set; }
        public long? ExcludedAuthKeyId { get; private set; }

        public Fixture()
        {
            var objectMessageSender = new Mock<IObjectMessageSender>(MockBehavior.Loose);
            objectMessageSender
                .Setup(p => p.PushMessageToPeerAsync(It.IsAny<Peer>(), It.IsAny<TUpdates>(), It.IsAny<long?>(),
                    It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<int>(), It.IsAny<int?>(),
                    It.IsAny<long>(), It.IsAny<PushData?>(), It.IsAny<List<long>?>()))
                .Callback((Peer peer, TUpdates updates, long? excludeAuthKeyId, long? _, long? _, long? _, int _,
                    int? _, long _, PushData? _, List<long>? _) =>
                {
                    PushedToPeer = peer;
                    PushedUpdates = updates;
                    ExcludedAuthKeyId = excludeAuthKeyId;
                })
                .Returns(Task.CompletedTask);

            var idGenerator = new Mock<IIdGenerator>(MockBehavior.Loose);
            idGenerator.Setup(p => p.NextLongIdAsync(It.IsAny<IdType>(), It.IsAny<long>(), It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);

            var userConverterService = new Mock<IUserConverterService>(MockBehavior.Loose);
            userConverterService
                .Setup(p => p.GetUserListAsync(It.IsAny<IRequestWithAccessHashKeyId>(), It.IsAny<List<long>>(),
                    It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<int>()))
                .ReturnsAsync((IRequestWithAccessHashKeyId _, List<long> userIds, bool _, bool _, int _) =>
                    [.. userIds.Select(ILayeredUser (id) => new TUser { Id = id })]);

            var draftMessageConverter = new Mock<IDraftMessageConverter>(MockBehavior.Loose);
            draftMessageConverter
                .Setup(p => p.ToDraftMessage(It.IsAny<Draft>()))
                .Returns((Draft draft) => new TDraftMessage
                {
                    Message = draft.Message,
                    Date = draft.Date,
                    Entities = new TVector<IMessageEntity>()
                });

            var layeredService = new Mock<ILayeredService<IDraftMessageConverter>>(MockBehavior.Loose);
            layeredService.Setup(p => p.GetConverter(It.IsAny<int>())).Returns(draftMessageConverter.Object);

            _handler = new DraftDomainEventHandler(objectMessageSender.Object,
                new Mock<ICommandBus>(MockBehavior.Loose).Object,
                idGenerator.Object,
                new Mock<IAckCacheService>(MockBehavior.Loose).Object,
                userConverterService.Object,
                new Mock<IChatConverterService>(MockBehavior.Loose).Object,
                layeredService.Object);
        }

        public TUpdateDraftMessage SingleUpdate()
        {
            return PushedUpdates.ShouldNotBeNull().Updates.ShouldHaveSingleItem().ShouldBeOfType<TUpdateDraftMessage>();
        }

        public Task HandleSavedAsync(Draft draft, Peer? peer = null)
        {
            var aggregateEvent = new DraftSavedEvent(RequestInfo(), OwnerUserId, peer ?? UserPeer, draft);

            return _handler.HandleAsync(DomainEvent(aggregateEvent), CancellationToken.None);
        }

        public Task HandleClearedAsync(List<DraftTopic> topics, bool withoutPeer = false)
        {
            var aggregateEvent = new DraftClearedEvent(RequestInfo(), OwnerUserId,
                withoutPeer ? null! : UserPeer, topics);

            return _handler.HandleAsync(DomainEvent(aggregateEvent), CancellationToken.None);
        }

        private static RequestInfo RequestInfo()
        {
            return MyTelegram.RequestInfo.Empty with { UserId = OwnerUserId, PermAuthKeyId = AuthKeyId };
        }

        private static IDomainEvent<DialogAggregate, DialogId, TEvent> DomainEvent<TEvent>(TEvent aggregateEvent)
            where TEvent : IAggregateEvent<DialogAggregate, DialogId>
        {
            var domainEvent = new Mock<IDomainEvent<DialogAggregate, DialogId, TEvent>>(MockBehavior.Loose);
            domainEvent.SetupGet(p => p.AggregateEvent).Returns(aggregateEvent);
            domainEvent.SetupGet(p => p.AggregateIdentity)
                .Returns(DialogId.Create(OwnerUserId, UserPeer));

            return domainEvent.Object;
        }
    }
}
