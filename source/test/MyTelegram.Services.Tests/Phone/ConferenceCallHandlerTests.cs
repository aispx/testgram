using System.Reflection;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger.Services.Caching;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;
using MyTelegram.Services.Services;

namespace MyTelegram.Services.Tests.Phone;

/// <summary>
/// Handler-level tests for the end-to-end (E2E) conference call handlers
/// (<c>CreateConferenceCallHandler</c>, <c>InviteConferenceCallParticipantHandler</c>,
/// <c>DeclineConferenceCallInviteHandler</c>, <c>DeleteConferenceCallParticipantsHandler</c>,
/// <c>GetGroupCallChainBlocksHandler</c>, <c>SendConferenceCallBroadcastHandler</c>,
/// <c>SendGroupCallEncryptedMessageHandler</c>).
///
/// Covers:
///   * Requirement 26.1 / 26.2 - create-and-join a conference (invite_link, first participant, chain block, media state).
///   * Requirement 26.3 / 26.4 - paged chain-block retrieval and <c>updateGroupCallChainBlocks</c> dispatch on block add.
///   * Requirement 27.1 / 27.2 / 27.3 - invite / decline / delete participant flows and their errors.
///   * Requirement 28.1 / 28.2 - conference broadcast distribution and encrypted-message relay.
/// </summary>
public class ConferenceCallHandlerTests
{
    private const long CreatorId = 1;
    private const long ParticipantUserId = 2;
    private const long InviteeUserId = 3;
    private const long OutsiderUserId = 99;
    private const long CallId = 700;
    private const long AccessHash = 55555;

    // ---- create (+ join) : R26.1 / R26.2 ---------------------------------------------------------

    [Fact]
    public async Task CreateConferenceCall_WithJoin_CreatesCallAddsCreatorStoresBlockAndMediaState()
    {
        var database = PhoneTestFixtures.CreateDatabase(out var store);
        var sender = new CapturingObjectMessageSender();

        var block = new byte[] { 1, 2, 3, 4 };
        var request = new RequestCreateConferenceCall
        {
            Join = true,
            Muted = true,
            VideoStopped = true,
            RandomId = 12345,
            PublicKey = new byte[32],
            Block = block,
            Params = new TDataJSON { Data = "{\"ssrc\":1}" }
        };

        var updates = await InvokeUpdatesAsync(CreateHandler("CreateConferenceCallHandler", NewIdGenerator(), database, new PeerHelper()), CreatorId, request);

        // R26.1: exactly one conference group call is created, carrying an invite link.
        var stored = LoadGroupCall(store);
        stored.Conference.ShouldBeTrue();
        stored.Active.ShouldBeTrue();
        stored.InviteHash.ShouldNotBeNullOrWhiteSpace();
        stored.InviteLink.ShouldNotBeNullOrWhiteSpace();
        stored.CreatorId.ShouldBe(CreatorId);
        stored.RandomId.ShouldBe(12345);

        // R26.2: the creator is added as the first participant with the supplied media state, and the
        // chain block is stored on sub-chain 0.
        var participant = stored.Participants.ShouldHaveSingleItem();
        participant.PeerId.ShouldBe(CreatorId);
        participant.Muted.ShouldBeTrue();
        participant.VideoStopped.ShouldBeTrue();
        participant.PublicKey.ShouldNotBeNull();
        stored.ChainBlocks.ShouldHaveSingleItem().Block.ShouldBe(block);

        // R26.1: an Updates with a groupCall (invite_link) is returned, alongside participant + chain-block updates.
        var tUpdates = updates.ShouldBeOfType<TUpdates>();
        var callUpdate = tUpdates.Updates.OfType<TUpdateGroupCall>().ShouldHaveSingleItem();
        var groupCall = callUpdate.Call.ShouldBeOfType<MyTelegram.Schema.TGroupCall>();
        groupCall.Conference.ShouldBeTrue();
        groupCall.InviteLink.ShouldNotBeNullOrWhiteSpace();
        tUpdates.Updates.OfType<TUpdateGroupCallParticipants>().ShouldHaveSingleItem();
        var chainUpdate = tUpdates.Updates.OfType<TUpdateGroupCallChainBlocks>().ShouldHaveSingleItem();
        chainUpdate.Blocks.ShouldHaveSingleItem().ToArray().ShouldBe(block);
    }

    [Fact]
    public async Task CreateConferenceCall_WithoutJoin_CreatesCallOnlyWithoutParticipantOrBlock()
    {
        var database = PhoneTestFixtures.CreateDatabase(out var store);

        var request = new RequestCreateConferenceCall { Join = false, RandomId = 777 };
        var updates = await InvokeUpdatesAsync(CreateHandler("CreateConferenceCallHandler", NewIdGenerator(), database, new PeerHelper()), CreatorId, request);

        var stored = LoadGroupCall(store);
        stored.Conference.ShouldBeTrue();
        stored.Participants.ShouldBeEmpty();
        stored.ChainBlocks.ShouldBeEmpty();

        var tUpdates = updates.ShouldBeOfType<TUpdates>();
        tUpdates.Updates.OfType<TUpdateGroupCall>().ShouldHaveSingleItem();
        tUpdates.Updates.OfType<TUpdateGroupCallParticipants>().ShouldBeEmpty();
        tUpdates.Updates.OfType<TUpdateGroupCallChainBlocks>().ShouldBeEmpty();
    }

    [Fact]
    public async Task CreateConferenceCall_DuplicateRandomId_ReturnsExistingWithoutCreatingAnother()
    {
        var database = PhoneTestFixtures.CreateDatabase(out var store);
        var idGenerator = NewIdGenerator();

        var request = new RequestCreateConferenceCall { Join = true, RandomId = 55, Block = new byte[] { 1 } };
        await InvokeUpdatesAsync(CreateHandler("CreateConferenceCallHandler", idGenerator, database, new PeerHelper()), CreatorId, request);

        // R26.1: re-using the same random_id for the same creator returns the existing call (no duplicate).
        var second = new RequestCreateConferenceCall { Join = true, RandomId = 55, Block = new byte[] { 1 } };
        var updates = await InvokeUpdatesAsync(CreateHandler("CreateConferenceCallHandler", idGenerator, database, new PeerHelper()), CreatorId, second);

        store.Count(PhoneTestFixtures.GroupCallsCollectionName).ShouldBe(1);
        updates.ShouldBeOfType<TUpdates>().Updates.OfType<TUpdateGroupCall>().ShouldHaveSingleItem();
    }

    // ---- invite : R27.1 --------------------------------------------------------------------------

    [Fact]
    public async Task InviteConferenceCallParticipant_RecordsInviteAndSendsConferenceCallMessage()
    {
        var database = PhoneTestFixtures.CreateDatabase(out var store);
        SeedConference(database);
        var sender = new CapturingObjectMessageSender();

        var request = new RequestInviteConferenceCallParticipant
        {
            Call = InputCall(),
            UserId = new TInputUser { UserId = InviteeUserId, AccessHash = 0 },
            Video = true
        };
        var updates = await InvokeUpdatesAsync(
            CreateHandler("InviteConferenceCallParticipantHandler", database, new PeerHelper(), NewIdGenerator(), sender),
            CreatorId,
            request);

        // R27.1: the invitation is recorded on the call.
        var stored = LoadGroupCall(store);
        stored.InvitedUserIds.ShouldContain(InviteeUserId);
        var inviteMessage = stored.InviteMessages.ShouldHaveSingleItem();
        inviteMessage.UserId.ShouldBe(InviteeUserId);
        inviteMessage.FromUserId.ShouldBe(CreatorId);

        // R27.1: an Updates carrying a messageActionConferenceCall invite message is returned to the inviter.
        var action = ConferenceCallActionOf(updates);
        action.CallId.ShouldBe(CallId);
        action.Video.ShouldBeTrue();

        // The invited user's active sessions receive the invite service message.
        var push = sender.PushesToUser(InviteeUserId).ShouldHaveSingleItem();
        push.Carries<TUpdateNewMessage>().ShouldBeTrue();
    }

    [Fact]
    public async Task InviteConferenceCallParticipant_UnknownCall_ThrowsGroupCallInvalid()
    {
        var database = PhoneTestFixtures.CreateDatabase(out _);
        var sender = new CapturingObjectMessageSender();

        var request = new RequestInviteConferenceCallParticipant
        {
            Call = InputCall(),
            UserId = new TInputUser { UserId = InviteeUserId, AccessHash = 0 }
        };
        var ex = await Should.ThrowAsync<RpcException>(() => InvokeUpdatesAsync(
            CreateHandler("InviteConferenceCallParticipantHandler", database, new PeerHelper(), NewIdGenerator(), sender),
            CreatorId,
            request));
        ex.Message.ShouldBe("GROUPCALL_INVALID"); // R27.5
    }

    // ---- decline : R27.2 -------------------------------------------------------------------------

    [Fact]
    public async Task DeclineConferenceCallInvite_MarksDeclinedAndNotifiesInviter()
    {
        const int msgId = 900;
        var database = PhoneTestFixtures.CreateDatabase(out var store);
        SeedConference(database, configure: call =>
        {
            call.InvitedUserIds.Add(ParticipantUserId);
            call.InviteMessages.Add(new GroupCallInviteMessageDoc
            {
                MessageId = msgId,
                UserId = ParticipantUserId,
                FromUserId = CreatorId,
                Video = false,
                Date = CurrentDate()
            });
        });
        var sender = new CapturingObjectMessageSender();

        var request = new RequestDeclineConferenceCallInvite { MsgId = msgId };
        var updates = await InvokeUpdatesAsync(
            CreateHandler("DeclineConferenceCallInviteHandler", database, NewPtsHelper(), sender),
            ParticipantUserId,
            request);

        // R27.2: the invitation is marked declined and removed from the invited set.
        var stored = LoadGroupCall(store);
        stored.InviteMessages.ShouldHaveSingleItem().Declined.ShouldBeTrue();
        stored.InvitedUserIds.ShouldNotContain(ParticipantUserId);

        // R27.2: an Updates editing the invite message to a missed conference call is returned.
        var tUpdates = updates.ShouldBeOfType<TUpdates>();
        var edit = tUpdates.Updates.OfType<TUpdateEditMessage>().ShouldHaveSingleItem();
        var action = ((TMessageService)edit.Message).Action.ShouldBeOfType<TMessageActionConferenceCall>();
        action.Missed.ShouldBeTrue();

        // The inviter is notified of the decline.
        sender.PushesToUser(CreatorId).ShouldHaveSingleItem().Carries<TUpdateEditMessage>().ShouldBeTrue();
    }

    [Fact]
    public async Task DeclineConferenceCallInvite_InvalidMsgId_ThrowsMessageIdInvalid()
    {
        var database = PhoneTestFixtures.CreateDatabase(out _);
        SeedConference(database);
        var sender = new CapturingObjectMessageSender();

        var request = new RequestDeclineConferenceCallInvite { MsgId = 0 };
        var ex = await Should.ThrowAsync<RpcException>(() => InvokeUpdatesAsync(
            CreateHandler("DeclineConferenceCallInviteHandler", database, NewPtsHelper(), sender),
            ParticipantUserId,
            request));
        ex.Message.ShouldBe("MESSAGE_ID_INVALID"); // R27.4
    }

    [Fact]
    public async Task DeclineConferenceCallInvite_UnknownInvite_ThrowsMessageIdInvalid()
    {
        var database = PhoneTestFixtures.CreateDatabase(out _);
        SeedConference(database);
        var sender = new CapturingObjectMessageSender();

        var request = new RequestDeclineConferenceCallInvite { MsgId = 4242 };
        var ex = await Should.ThrowAsync<RpcException>(() => InvokeUpdatesAsync(
            CreateHandler("DeclineConferenceCallInviteHandler", database, NewPtsHelper(), sender),
            ParticipantUserId,
            request));
        ex.Message.ShouldBe("MESSAGE_ID_INVALID"); // R27.4
    }

    // ---- delete participants : R27.3 -------------------------------------------------------------

    [Fact]
    public async Task DeleteConferenceCallParticipants_RemovesParticipantsAppendsBlockAndFansOut()
    {
        var database = PhoneTestFixtures.CreateDatabase(out var store);
        SeedConference(database, configure: call =>
        {
            call.Participants.Add(Participant(ParticipantUserId, source: 111));
            call.Participants.Add(Participant(InviteeUserId, source: 112));
        });
        var sender = new CapturingObjectMessageSender();

        var block = new byte[] { 7, 7, 7 };
        var request = new RequestDeleteConferenceCallParticipants
        {
            Call = InputCall(),
            Kick = true,
            Ids = new TVector<long> { ParticipantUserId },
            Block = block
        };
        var updates = await InvokeUpdatesAsync(
            CreateHandler("DeleteConferenceCallParticipantsHandler", database, new PeerHelper(), sender),
            CreatorId,
            request);

        // R27.3: the listed participant is removed, the chain block is appended.
        var stored = LoadGroupCall(store);
        stored.Participants.ShouldNotContain(p => p.PeerId == ParticipantUserId);
        stored.Participants.ShouldContain(p => p.PeerId == InviteeUserId);
        stored.ChainBlocks.ShouldHaveSingleItem().Block.ShouldBe(block);

        // R27.3: an Updates with the removed-participant + chain-block updates is returned.
        var tUpdates = updates.ShouldBeOfType<TUpdates>();
        var participantsUpdate = tUpdates.Updates.OfType<TUpdateGroupCallParticipants>().ShouldHaveSingleItem();
        participantsUpdate.Participants.ShouldContain(p => ((TGroupCallParticipant)p).Left);
        tUpdates.Updates.OfType<TUpdateGroupCallChainBlocks>().ShouldHaveSingleItem();

        // The removal is fanned out to remaining/removed subscribers, excluding the originating admin.
        sender.PushesToUser(InviteeUserId).ShouldNotBeEmpty();
        sender.PushesToUser(ParticipantUserId).ShouldNotBeEmpty();
        sender.PushesToUser(CreatorId).ShouldBeEmpty();
    }

    [Fact]
    public async Task DeleteConferenceCallParticipants_WithoutExactlyOneFlag_ThrowsGroupCallInvalid()
    {
        var database = PhoneTestFixtures.CreateDatabase(out _);
        SeedConference(database);
        var sender = new CapturingObjectMessageSender();

        // Neither only_left nor kick is set (both false) - exactly one is required.
        var request = new RequestDeleteConferenceCallParticipants
        {
            Call = InputCall(),
            Ids = new TVector<long> { ParticipantUserId },
            Block = new byte[] { 1 }
        };
        var ex = await Should.ThrowAsync<RpcException>(() => InvokeUpdatesAsync(
            CreateHandler("DeleteConferenceCallParticipantsHandler", database, new PeerHelper(), sender),
            CreatorId,
            request));
        ex.Message.ShouldBe("GROUPCALL_INVALID");
    }

    // ---- chain-block paging : R26.3 --------------------------------------------------------------

    [Fact]
    public async Task GetGroupCallChainBlocks_ReturnsRequestedPageWithNextOffset()
    {
        var database = PhoneTestFixtures.CreateDatabase(out _);
        SeedConference(database, configure: call =>
        {
            for (var i = 0; i < 5; i++)
            {
                call.ChainBlocks.Add(new GroupCallChainBlockDoc { SubChainId = 0, Block = new[] { (byte)i } });
            }
        });

        var handler = CreateHandler("GetGroupCallChainBlocksHandler", database);

        // R26.3: first page.
        var firstPage = await InvokeUpdatesAsync(handler, CreatorId,
            new RequestGetGroupCallChainBlocks { Call = InputCall(), SubChainId = 0, Offset = 0, Limit = 2 });
        var firstChain = ChainBlocksOf(firstPage);
        firstChain.SubChainId.ShouldBe(0);
        firstChain.Blocks.Select(b => b.ToArray()[0]).ShouldBe(new byte[] { 0, 1 });
        firstChain.NextOffset.ShouldBe(2);

        // R26.3: second page continues from the returned offset.
        var secondPage = await InvokeUpdatesAsync(handler, CreatorId,
            new RequestGetGroupCallChainBlocks { Call = InputCall(), SubChainId = 0, Offset = 2, Limit = 2 });
        var secondChain = ChainBlocksOf(secondPage);
        secondChain.Blocks.Select(b => b.ToArray()[0]).ShouldBe(new byte[] { 2, 3 });
        secondChain.NextOffset.ShouldBe(4);

        // R26.3: final (partial) page.
        var thirdPage = await InvokeUpdatesAsync(handler, CreatorId,
            new RequestGetGroupCallChainBlocks { Call = InputCall(), SubChainId = 0, Offset = 4, Limit = 2 });
        var thirdChain = ChainBlocksOf(thirdPage);
        thirdChain.Blocks.Select(b => b.ToArray()[0]).ShouldBe(new byte[] { 4 });
        thirdChain.NextOffset.ShouldBe(5);
    }

    [Fact]
    public async Task GetGroupCallChainBlocks_NonConferenceCall_ThrowsGroupCallInvalid()
    {
        var database = PhoneTestFixtures.CreateDatabase(out _);
        SeedConference(database, conference: false);

        var ex = await Should.ThrowAsync<RpcException>(() => InvokeUpdatesAsync(
            CreateHandler("GetGroupCallChainBlocksHandler", database),
            CreatorId,
            new RequestGetGroupCallChainBlocks { Call = InputCall(), SubChainId = 0, Offset = 0, Limit = 10 }));
        ex.Message.ShouldBe("GROUPCALL_INVALID"); // R26.5
    }

    // ---- broadcast : R28.1 + updateGroupCallChainBlocks dispatch ---------------------------------

    [Fact]
    public async Task SendConferenceCallBroadcast_StoresBlockAndDispatchesChainBlocksUpdate()
    {
        var database = PhoneTestFixtures.CreateDatabase(out var store);
        SeedConference(database, configure: call => call.Participants.Add(Participant(ParticipantUserId, source: 111)));
        var sender = new CapturingObjectMessageSender();

        var block = new byte[] { 5, 6 };
        var request = new RequestSendConferenceCallBroadcast { Call = InputCall(), Block = block };
        var updates = await InvokeUpdatesAsync(
            CreateHandler("SendConferenceCallBroadcastHandler", database, sender),
            CreatorId,
            request);

        // R28.1: the broadcast block is stored (sub-chain 1) and returned as an Updates.
        var stored = LoadGroupCall(store);
        stored.ChainBlocks.ShouldContain(b => b.SubChainId == 1);
        var chain = ChainBlocksOf(updates);
        chain.SubChainId.ShouldBe(1);
        chain.Blocks.ShouldHaveSingleItem().ToArray().ShouldBe(block);

        // R26.4: an updateGroupCallChainBlocks is delivered to the other participant, not the sender.
        sender.PushesToUser(ParticipantUserId).ShouldContain(p => p.Carries<TUpdateGroupCallChainBlocks>());
        sender.PushesToUser(CreatorId).ShouldBeEmpty();
    }

    [Fact]
    public async Task SendConferenceCallBroadcast_ByNonParticipant_ThrowsGroupCallInvalid()
    {
        var database = PhoneTestFixtures.CreateDatabase(out _);
        SeedConference(database, configure: call => call.Participants.Add(Participant(ParticipantUserId, source: 111)));
        var sender = new CapturingObjectMessageSender();

        var request = new RequestSendConferenceCallBroadcast { Call = InputCall(), Block = new byte[] { 1 } };
        var ex = await Should.ThrowAsync<RpcException>(() => InvokeUpdatesAsync(
            CreateHandler("SendConferenceCallBroadcastHandler", database, sender),
            OutsiderUserId,
            request));
        ex.Message.ShouldBe("GROUPCALL_INVALID");
    }

    // ---- encrypted-message relay : R28.2 ---------------------------------------------------------

    [Fact]
    public async Task SendGroupCallEncryptedMessage_RelaysToParticipantsAndReturnsBoolTrue()
    {
        var database = PhoneTestFixtures.CreateDatabase(out _);
        SeedConference(database, configure: call => call.Participants.Add(Participant(ParticipantUserId, source: 111)));
        var sender = new CapturingObjectMessageSender();

        var message = new byte[] { 8, 9, 10 };
        var request = new RequestSendGroupCallEncryptedMessage { Call = InputCall(), EncryptedMessage = message };
        var result = await InvokeAsync(
            CreateHandler("SendGroupCallEncryptedMessageHandler", database, sender),
            CreatorId,
            request);

        // R28.2: boolTrue is returned.
        result.ShouldBeOfType<TBoolTrue>();

        // R28.2: the encrypted message is relayed to the other participant, not echoed to the sender.
        var push = sender.PushesToUser(ParticipantUserId).ShouldHaveSingleItem();
        var relay = push.Updates.OfType<TUpdateGroupCallEncryptedMessage>().ShouldHaveSingleItem();
        relay.EncryptedMessage.ToArray().ShouldBe(message);
        ((TPeerUser)relay.FromId).UserId.ShouldBe(CreatorId);
        sender.PushesToUser(CreatorId).ShouldBeEmpty();
    }

    [Fact]
    public async Task SendGroupCallEncryptedMessage_InactiveCall_ThrowsGroupCallInvalid()
    {
        var database = PhoneTestFixtures.CreateDatabase(out _);
        SeedConference(database, active: false);
        var sender = new CapturingObjectMessageSender();

        var request = new RequestSendGroupCallEncryptedMessage { Call = InputCall(), EncryptedMessage = new byte[] { 1 } };
        var ex = await Should.ThrowAsync<RpcException>(() => InvokeAsync(
            CreateHandler("SendGroupCallEncryptedMessageHandler", database, sender),
            CreatorId,
            request));
        ex.Message.ShouldBe("GROUPCALL_INVALID"); // R28.5
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static IInputGroupCall InputCall() => new TInputGroupCall { Id = CallId, AccessHash = AccessHash };

    private static int CurrentDate() => (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static GroupCallParticipantDoc Participant(long userId, int source) => new()
    {
        UserId = userId,
        PeerId = userId,
        PeerType = (int)PeerType.User,
        Source = source,
        Date = CurrentDate()
    };

    private static void SeedConference(
        IMongoDatabase database,
        bool active = true,
        bool conference = true,
        Action<GroupCallDocument>? configure = null)
    {
        var call = new GroupCallDocument
        {
            Id = CallId,
            CallId = CallId,
            AccessHash = AccessHash,
            CreatorId = CreatorId,
            PeerId = CreatorId,
            PeerType = (int)PeerType.User,
            Active = active,
            Conference = conference,
            Version = 1,
            Date = CurrentDate(),
            InviteHash = "invitehash",
            InviteLink = "https://t.me/call/invitehash"
        };
        configure?.Invoke(call);
        database.GetCollection<GroupCallDocument>(PhoneTestFixtures.GroupCallsCollectionName).InsertOne(call);
    }

    private static GroupCallDocument LoadGroupCall(InMemoryMongoStore store)
    {
        var doc = store.Documents(PhoneTestFixtures.GroupCallsCollectionName).Single();
        return BsonSerializer.Deserialize<GroupCallDocument>(doc);
    }

    private static TMessageActionConferenceCall ConferenceCallActionOf(IUpdates updates)
    {
        var tUpdates = updates.ShouldBeOfType<TUpdates>();
        var newMessage = tUpdates.Updates.OfType<TUpdateNewMessage>().ShouldHaveSingleItem();
        return ((TMessageService)newMessage.Message).Action.ShouldBeOfType<TMessageActionConferenceCall>();
    }

    private static TUpdateGroupCallChainBlocks ChainBlocksOf(IUpdates updates)
    {
        var tUpdates = updates.ShouldBeOfType<TUpdates>();
        return tUpdates.Updates.OfType<TUpdateGroupCallChainBlocks>().ShouldHaveSingleItem();
    }

    private static IIdGenerator NewIdGenerator()
    {
        var next = 1000;
        var mock = new Mock<IIdGenerator>();
        mock.Setup(x => x.NextIdAsync(It.IsAny<IdType>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => next++);
        mock.Setup(x => x.NextLongIdAsync(It.IsAny<IdType>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => next++);
        return mock.Object;
    }

    private static IPtsHelper NewPtsHelper()
    {
        var mock = new Mock<IPtsHelper>();
        mock.Setup(x => x.GetCachedPts(It.IsAny<long>())).Returns(0);
        mock.Setup(x => x.IncrementPtsAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<long>(), It.IsAny<int>()))
            .ReturnsAsync(1);
        return mock.Object;
    }

    private static object CreateHandler(string handlerTypeName, params object[] args)
    {
        var assembly = typeof(GroupCallDocument).Assembly;
        var type = assembly.GetType($"MyTelegram.Messenger.Handlers.LatestLayer.Phone.{handlerTypeName}", throwOnError: true)!;
        return PhoneTestFixtures.CreateGroupCallHandler(type, args);
    }

    private static async Task<IUpdates> InvokeUpdatesAsync(object handler, long userId, IObject request)
        => (IUpdates)await InvokeAsync(handler, userId, request);

    private static async Task<IObject> InvokeAsync(object handler, long userId, IObject request)
    {
        var method = handler.GetType().GetMethod("HandleAsync", new[] { typeof(IRequestInput), typeof(IObject) })!;
        var input = PhoneTestFixtures.RequestInput(userId).Build();
        object taskObj;
        try
        {
            taskObj = method.Invoke(handler, new object[] { input, request })!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }

        var result = await (Task<IObject>)taskObj;
        return ((TRpcResult)result).Result;
    }
}
