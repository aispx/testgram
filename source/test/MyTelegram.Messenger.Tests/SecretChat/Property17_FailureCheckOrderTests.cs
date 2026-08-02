using FsCheck;
using FsCheck.Xunit;
using MyTelegram;
using MyTelegram.Domain.Aggregates.EncryptedChat;
using MyTelegram.Messenger.Services.Caching;
using MyTelegram.Messenger.Services.SecretChat;
using MyTelegram.Queries;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.SecretChat;

/// <summary>
/// Feature: secret-chats, Property 17: requestEncryption failure check order.
///
/// For any <c>messages.requestEncryption</c> whose target simultaneously does not exist AND has blocked
/// the caller, the returned error is 400 USER_ID_INVALID — user resolution is checked strictly before the
/// block check. Stated in full, the fixed precedence is: (1) target resolution (an InputUserSelf, the
/// caller itself, an unregistered id, or a bot) -> USER_ID_INVALID, (2) the target is deleted ->
/// INPUT_USER_DEACTIVATED, (3) the target has blocked the caller -> 403 USER_IS_BLOCKED, (4) g_a is
/// outside the DH range -> DH_G_A_INVALID. Whichever violated check comes first in that order decides the
/// error; when none is violated the request succeeds.
///
/// Feature: secret-chats, Property 18: sendEncryptedService failure check order.
///
/// For any <c>messages.sendEncryptedService</c> issued against a chat that is simultaneously Discarded AND
/// whose other participant is deleted, the returned error is 400 ENCRYPTION_DECLINED — the chat-state
/// check precedes the deleted-participant check. Stated in full, the fixed precedence is: (1) the ordered
/// access resolution, (2) RequireActive (Discarded -> ENCRYPTION_DECLINED, Waiting -> ENCRYPTION_ID_INVALID),
/// (3) the other participant is deleted -> 403 USER_DELETED.
///
/// Validates: Requirements 3.4, 3.5, 3.7 (Property 17) and Requirements 8.4, 8.7 (Property 18).
///
/// How this is tested. Each property generates a case record that toggles every violable condition
/// INDEPENDENTLY, so the generator sweeps the full lattice of simultaneous violations rather than one
/// failure at a time:
/// <list type="bullet">
/// <item><see cref="SecretChatRequestFailureCase"/> — the target kind (registered user / unregistered id /
/// the caller by id / <c>inputUserSelf</c> / a bot), whether the target is flagged deleted, whether the
/// target has blocked the caller, and whether g_a lies inside the DH range; plus a nuisance random_id.
/// The blocked relation and the invalid g_a are installed even in the cases whose target cannot resolve,
/// which is exactly what makes the ordering observable.</item>
/// <item><see cref="SecretChatServiceFailureCase"/> — the stored chat state (Waiting / Active / Discarded;
/// "Requested" is a converter view and is never stored), whether the other participant is flagged deleted,
/// which side of the chat sends, and a nuisance random_id and payload.</item>
/// </list>
/// Every case drives the REAL <see cref="SecretChatAppService"/> wired to the REAL
/// <see cref="SecretChatAccessResolver"/> over the hand-written harness fakes; only the transport, the
/// stores, the id generator and the block cache are substituted. The expected error is recomputed
/// independently of the production code from the fixed precedence alone
/// (<see cref="Property17_FailureCheckOrderTests.ExpectedRequestError"/> /
/// <see cref="Property17_FailureCheckOrderTests.ExpectedServiceError"/>) and compared against
/// <see cref="RpcException.RpcError"/>. Ordering is additionally asserted STRUCTURALLY rather than only
/// through the error string: the fake query processor records every query it issues (so a target that
/// never resolves must leave the second user lookup unissued, and a non-Active chat must leave the
/// other-participant lookup unissued) and the block cache counts its calls (so an unresolvable target must
/// leave it untouched). Every failure additionally asserts the "nothing happened" invariants — no
/// aggregate command, no dispatched update, no stored blob, no reserved ledger row and no burned qts.
/// The fully valid cases assert the success shape (a <see cref="TEncryptedChatWaiting"/> for
/// requestEncryption, a stored + dispatched message for sendEncryptedService). Two explicit facts pin the
/// exact conjunctions named by the properties. Each property runs a minimum of 100 generated cases.
/// </summary>
[Properties(Arbitrary = new[] { typeof(SecretChatFailureOrderArbitraries) }, MaxTest = 100)]
public class Property17_FailureCheckOrderTests
{
    /// <summary>A user id that is never registered in the fake query processor.</summary>
    private const long UnregisteredUserId = 909090;

    // =====================================================================================
    // Property 17: requestEncryption failure check order
    // =====================================================================================

    // 200 draws over a 5 x 2 x 2 x 2 = 40 combination space so every conjunction is hit repeatedly.
    [Property(Arbitrary = new[] { typeof(SecretChatFailureOrderArbitraries) }, MaxTest = 200)]
    public void RequestEncryption_reports_the_earliest_violated_check(SecretChatRequestFailureCase @case)
    {
        var world = new RequestWorld(@case);
        var expectedError = ExpectedRequestError(@case);

        if (expectedError == null)
        {
            // ---- Nothing is violated: the request goes through ---------------------------------
            var result = world.Invoke();

            var waiting = result.ShouldBeOfType<TEncryptedChatWaiting>();
            waiting.Id.ShouldBe(SecretChatTestHarness.ChatId);
            waiting.AdminId.ShouldBe(SecretChatTestHarness.AdminId);
            waiting.ParticipantId.ShouldBe(SecretChatTestHarness.ParticipantId);
            waiting.AccessHash.ShouldNotBe(0L);

            world.CommandBus.Published.ShouldHaveSingleItem().ShouldBeOfType<CreateEncryptedChatCommand>();

            // encryptedChatRequested fans out to ALL of the target's devices — none is bound yet.
            var dispatched = world.Dispatcher.Dispatched.ShouldHaveSingleItem();
            dispatched.UserId.ShouldBe(SecretChatTestHarness.ParticipantId);
            dispatched.OnlySendToThisAuthKeyId.ShouldBeNull();
            dispatched.ExcludeAuthKeyId.ShouldBeNull();
            dispatched.Update.ShouldBeOfType<TUpdateEncryption>().Chat.ShouldBeOfType<TEncryptedChatRequested>();

            // The ledger reserved the (adminId, random_id) row that makes a retry idempotent.
            world.Ledger.FindAsync(SecretChatTestHarness.AdminId, @case.RandomId).GetAwaiter().GetResult()
                .ShouldNotBeNull();
        }
        else
        {
            var ex = Should.Throw<RpcException>(() => world.Invoke());
            ex.RpcError.ShouldBe(expectedError.Value);

            // A rejected request creates nothing, reserves nothing and notifies nobody.
            world.CommandBus.Published.ShouldBeEmpty();
            world.Dispatcher.Dispatched.ShouldBeEmpty();
            world.Ledger.FindAsync(SecretChatTestHarness.AdminId, @case.RandomId).GetAwaiter().GetResult()
                .ShouldBeNull();
        }

        // Structural half of the ordering claim: the checks that were actually PERFORMED.
        // A target that cannot resolve — or that resolves but is deleted — must leave the block cache
        // untouched, and an InputUserSelf / self-by-id target must not even reach the target lookup.
        world.QueryProcessor.ExecutedQueries.ShouldBe(ExpectedRequestQueryTrace(@case));
        world.BlockCache.IsBlockedCallCount
            .ShouldBe(TargetResolves(@case.Target) && !@case.TargetIsDeleted ? 1 : 0);
    }

    /// <summary>
    /// The conjunction named by Property 17, pinned as an example: the target simultaneously does not
    /// exist AND has blocked the caller — user resolution wins, so USER_ID_INVALID is returned and the
    /// block cache is never consulted.
    /// </summary>
    [Fact]
    public void RequestEncryption_prefers_user_id_invalid_over_user_is_blocked()
    {
        var world = new RequestWorld(new SecretChatRequestFailureCase(SecretChatRequestTargetKind.Missing,
            TargetIsDeleted: true,
            TargetBlockedCaller: true,
            GaValid: false,
            RandomId: 4242));

        var ex = Should.Throw<RpcException>(() => world.Invoke());

        ex.RpcError.ShouldBe(RpcErrors.RpcErrors400.UserIdInvalid);
        world.BlockCache.IsBlockedCallCount.ShouldBe(0);
        world.CommandBus.Published.ShouldBeEmpty();
        world.Dispatcher.Dispatched.ShouldBeEmpty();
    }

    /// <summary>
    /// The block check still precedes the DH-range check: a blocked caller supplying an out-of-range g_a
    /// gets USER_IS_BLOCKED, not DH_G_A_INVALID.
    /// </summary>
    [Fact]
    public void RequestEncryption_prefers_user_is_blocked_over_dh_g_a_invalid()
    {
        var world = new RequestWorld(new SecretChatRequestFailureCase(SecretChatRequestTargetKind.Existing,
            TargetIsDeleted: false,
            TargetBlockedCaller: true,
            GaValid: false,
            RandomId: 77));

        Should.Throw<RpcException>(() => world.Invoke()).RpcError
            .ShouldBe(RpcErrors.RpcErrors403.UserIsBlocked);
    }

    /// <summary>
    /// The RPC error of the earliest violated requestEncryption check, derived from the fixed precedence
    /// alone: target resolution -> deleted target -> blocked caller -> DH range. <c>null</c> when nothing
    /// is violated.
    /// </summary>
    private static RpcError? ExpectedRequestError(SecretChatRequestFailureCase @case)
    {
        // (1) Target resolution: inputUserSelf, the caller itself, an unregistered id or a bot.
        if (!TargetResolves(@case.Target))
        {
            return RpcErrors.RpcErrors400.UserIdInvalid;
        }

        // (2) Deleted target.
        if (@case.TargetIsDeleted)
        {
            return RpcErrors.RpcErrors400.InputUserDeactivated;
        }

        // (3) The target has blocked the caller.
        if (@case.TargetBlockedCaller)
        {
            return RpcErrors.RpcErrors403.UserIsBlocked;
        }

        // (4) g_a outside the DH safety range.
        if (!@case.GaValid)
        {
            return RpcErrors.RpcErrors400.DhGAInvalid;
        }

        return null;
    }

    /// <summary>
    /// Queries the request path must have issued: always the caller lookup (the caller-type check), plus
    /// the target lookup only when the InputUser carries a foreign, non-zero id.
    /// </summary>
    private static string[] ExpectedRequestQueryTrace(SecretChatRequestFailureCase @case)
    {
        return @case.Target is SecretChatRequestTargetKind.InputUserSelf or SecretChatRequestTargetKind.SelfById
            ? [nameof(GetUserByIdQuery)]
            : [nameof(GetUserByIdQuery), nameof(GetUserByIdQuery)];
    }

    private static bool TargetResolves(SecretChatRequestTargetKind target)
    {
        return target == SecretChatRequestTargetKind.Existing;
    }

    // =====================================================================================
    // Property 18: sendEncryptedService failure check order
    // =====================================================================================

    // 200 draws over a 3 x 2 x 2 = 12 combination space so every conjunction is hit repeatedly.
    [Property(Arbitrary = new[] { typeof(SecretChatFailureOrderArbitraries) }, MaxTest = 200)]
    public void SendEncryptedService_reports_the_earliest_violated_check(SecretChatServiceFailureCase @case)
    {
        var world = new ServiceWorld(@case);
        var expectedError = ExpectedServiceError(@case);

        if (expectedError == null)
        {
            // ---- Active chat, live participant: the service message is delivered ---------------
            var result = world.Invoke();

            result.ShouldBeOfType<MyTelegram.Schema.Messages.TSentEncryptedMessage>();
            world.MessageStore.All.Count.ShouldBe(1);

            var dispatched = world.Dispatcher.Dispatched.ShouldHaveSingleItem();
            dispatched.Update.ShouldBeOfType<TUpdateNewEncryptedMessage>();
            dispatched.UserId.ShouldBe(world.OtherUserId);
            dispatched.OnlySendToThisAuthKeyId.ShouldBe(world.OtherPermAuthKeyId);
            dispatched.Qts.ShouldBe(SecretChatConsts.QtsInitialValue);

            // Service messages are protocol-level and must never raise a user-visible notification.
            dispatched.PushData.ShouldBeNull();

            // Sending does not mutate the chat aggregate.
            world.CommandBus.Published.ShouldBeEmpty();
        }
        else
        {
            var ex = Should.Throw<RpcException>(() => world.Invoke());
            ex.RpcError.ShouldBe(expectedError.Value);

            // Nothing stored, nothing delivered, no state change and no qts burned on either device.
            world.MessageStore.All.ShouldBeEmpty();
            world.Dispatcher.Dispatched.ShouldBeEmpty();
            world.CommandBus.Published.ShouldBeEmpty();

            world.MessageStore
                .GetHighestQtsAsync(SecretChatTestHarness.AdminId, SecretChatTestHarness.AdminPermAuthKeyId)
                .GetAwaiter().GetResult()
                .ShouldBe(SecretChatConsts.QtsInitialValue - 1);
            world.MessageStore
                .GetHighestQtsAsync(SecretChatTestHarness.ParticipantId,
                    SecretChatTestHarness.ParticipantPermAuthKeyId)
                .GetAwaiter().GetResult()
                .ShouldBe(SecretChatConsts.QtsInitialValue - 1);
        }

        // Structural half of the ordering claim: a non-Active chat must never reach the lookup that
        // decides USER_DELETED.
        world.QueryProcessor.ExecutedQueries.ShouldBe(ExpectedServiceQueryTrace(@case));
    }

    /// <summary>
    /// The conjunction named by Property 18, pinned as an example: the chat is Discarded AND the other
    /// participant is deleted — the state check wins, so ENCRYPTION_DECLINED is returned and the
    /// other-participant lookup is never issued.
    /// </summary>
    [Fact]
    public void SendEncryptedService_prefers_encryption_declined_over_user_deleted()
    {
        var world = new ServiceWorld(new SecretChatServiceFailureCase(ChatState.Discarded,
            OtherParticipantDeleted: true,
            CallerIsAdmin: true,
            RandomId: 9001,
            Data: [1, 2, 3, 4]));

        var ex = Should.Throw<RpcException>(() => world.Invoke());

        ex.RpcError.ShouldBe(RpcErrors.RpcErrors400.EncryptionDeclined);
        world.QueryProcessor.ExecutedQueries
            .ShouldBe(new[] { nameof(GetUserByIdQuery), nameof(GetEncryptedChatByIdQuery) });
        world.MessageStore.All.ShouldBeEmpty();
        world.Dispatcher.Dispatched.ShouldBeEmpty();
    }

    /// <summary>
    /// The other half of the same ordering: once the chat IS Active, the deleted participant surfaces as
    /// 403 USER_DELETED.
    /// </summary>
    [Fact]
    public void SendEncryptedService_reports_user_deleted_when_the_chat_is_active()
    {
        var world = new ServiceWorld(new SecretChatServiceFailureCase(ChatState.Active,
            OtherParticipantDeleted: true,
            CallerIsAdmin: true,
            RandomId: 9002,
            Data: [1, 2, 3, 4]));

        var ex = Should.Throw<RpcException>(() => world.Invoke());

        ex.RpcError.ShouldBe(RpcErrors.RpcErrors403.UserDeleted);
        world.MessageStore.All.ShouldBeEmpty();
        world.Dispatcher.Dispatched.ShouldBeEmpty();
    }

    /// <summary>
    /// The last step of the same precedence, pinned at the boundary: with the chat Active and both
    /// accounts live, an envelope one byte below the structural floor is rejected as DATA_INVALID and
    /// burns no qts on the recipient's device.
    /// </summary>
    [Fact]
    public void SendEncryptedService_rejects_an_undersized_envelope_as_data_invalid()
    {
        var world = new ServiceWorld(new SecretChatServiceFailureCase(ChatState.Active,
            OtherParticipantDeleted: false,
            CallerIsAdmin: true,
            RandomId: 9003,
            Data: new byte[SecretChatConsts.MinEncryptedPayloadLength - 1]));

        var ex = Should.Throw<RpcException>(() => world.Invoke());

        ex.RpcError.ShouldBe(RpcErrors.RpcErrors400.DataInvalid);
        world.MessageStore.All.ShouldBeEmpty();
        world.Dispatcher.Dispatched.ShouldBeEmpty();
        world.MessageStore
            .GetHighestQtsAsync(SecretChatTestHarness.ParticipantId,
                SecretChatTestHarness.ParticipantPermAuthKeyId)
            .GetAwaiter().GetResult()
            .ShouldBe(SecretChatConsts.QtsInitialValue - 1);
    }

    /// <summary>
    /// The other side of that boundary: exactly <see cref="SecretChatConsts.MinEncryptedPayloadLength"/>
    /// bytes is the smallest structurally valid envelope and is relayed.
    /// </summary>
    [Fact]
    public void SendEncryptedService_accepts_an_envelope_at_the_structural_floor()
    {
        var world = new ServiceWorld(new SecretChatServiceFailureCase(ChatState.Active,
            OtherParticipantDeleted: false,
            CallerIsAdmin: true,
            RandomId: 9004,
            Data: new byte[SecretChatConsts.MinEncryptedPayloadLength]));

        world.Invoke().ShouldBeOfType<MyTelegram.Schema.Messages.TSentEncryptedMessage>();

        world.MessageStore.All.Count.ShouldBe(1);
        world.Dispatcher.Dispatched.ShouldHaveSingleItem()
            .Update.ShouldBeOfType<TUpdateNewEncryptedMessage>();
    }

    /// <summary>
    /// The RPC error of the earliest violated sendEncryptedService check, derived from the fixed
    /// precedence alone: RequireActive (Waiting -> ENCRYPTION_ID_INVALID, Discarded -> ENCRYPTION_DECLINED)
    /// -> deleted other participant -> USER_DELETED -> undersized envelope -> DATA_INVALID.
    /// <c>null</c> when nothing is violated.
    /// </summary>
    private static RpcError? ExpectedServiceError(SecretChatServiceFailureCase @case)
    {
        // (1) The chat state check runs first, for a send operation.
        if (@case.State == ChatState.Waiting)
        {
            return RpcErrors.RpcErrors400.EncryptionIdInvalid;
        }

        if (@case.State == ChatState.Discarded)
        {
            return RpcErrors.RpcErrors400.EncryptionDeclined;
        }

        // (2) Only then is the other participant's account state consulted.
        if (@case.OtherParticipantDeleted)
        {
            return RpcErrors.RpcErrors403.UserDeleted;
        }

        // (3) Last, the shape of the opaque envelope: too short to hold key_fingerprint + msg_key +
        // one AES block, so no client could ever decrypt it.
        if (@case.Data.Length < SecretChatConsts.MinEncryptedPayloadLength)
        {
            return RpcErrors.RpcErrors400.DataInvalid;
        }

        return null;
    }

    /// <summary>
    /// Queries the sendEncryptedService path must have issued: the caller lookup and the chat lookup
    /// always; the other-participant lookup only once the chat has passed the Active check.
    /// </summary>
    private static string[] ExpectedServiceQueryTrace(SecretChatServiceFailureCase @case)
    {
        return @case.State == ChatState.Active
            ? [nameof(GetUserByIdQuery), nameof(GetEncryptedChatByIdQuery), nameof(GetUserByIdQuery)]
            : [nameof(GetUserByIdQuery), nameof(GetEncryptedChatByIdQuery)];
    }

    // =====================================================================================
    // Worlds
    // =====================================================================================

    /// <summary>
    /// One fully wired requestEncryption scenario: the REAL service + REAL access resolver over the
    /// harness fakes, with the case's conditions installed.
    /// </summary>
    private sealed class RequestWorld
    {
        private readonly SecretChatAppService _service;
        private readonly TestRequestInput _input;
        private readonly IInputUser _targetInputUser;
        private readonly byte[] _ga;
        private readonly int _randomId;

        public RequestWorld(SecretChatRequestFailureCase @case)
        {
            QueryProcessor = new FakeQueryProcessor();
            CommandBus = new RecordingCommandBus();
            Dispatcher = new RecordingUpdateDispatcher();
            Ledger = new InMemorySecretChatRequestLedger();
            BlockCache = new CountingBlockCacheAppService();

            // The caller is always a valid, registered, non-bot user: this property is about the checks
            // that come AFTER the caller-type check (which Property 3 covers).
            QueryProcessor.Users[SecretChatTestHarness.AdminId] = FakeUser.Create(SecretChatTestHarness.AdminId);

            // The target the InputUser points at, per generated kind.
            var targetUserId = @case.Target switch
            {
                SecretChatRequestTargetKind.InputUserSelf => 0,
                SecretChatRequestTargetKind.SelfById => SecretChatTestHarness.AdminId,
                SecretChatRequestTargetKind.Missing => UnregisteredUserId,
                _ => SecretChatTestHarness.ParticipantId
            };

            _targetInputUser = @case.Target == SecretChatRequestTargetKind.InputUserSelf
                ? new TInputUserSelf()
                : new TInputUser { UserId = targetUserId, AccessHash = 0 };

            // Register the target read model for the kinds that have one. "Missing" deliberately has none;
            // "SelfById" reuses the already registered caller.
            if (@case.Target is SecretChatRequestTargetKind.Existing or SecretChatRequestTargetKind.Bot)
            {
                QueryProcessor.Users[SecretChatTestHarness.ParticipantId] = FakeUser.Create(
                    SecretChatTestHarness.ParticipantId,
                    bot: @case.Target == SecretChatRequestTargetKind.Bot,
                    isDeleted: @case.TargetIsDeleted ? true : null);
            }

            // The block relation is installed even when the target cannot resolve — that is precisely
            // what makes "user resolution before block check" observable.
            if (@case.TargetBlockedCaller)
            {
                var blockerId = targetUserId == 0 ? SecretChatTestHarness.ParticipantId : targetUserId;
                BlockCache.Blocks.Add((blockerId, SecretChatTestHarness.AdminId));
            }

            _ga = @case.GaValid ? SecretChatTestHarness.ValidDhValue() : @case.InvalidGa;
            _randomId = @case.RandomId;
            _input = SecretChatTestHarness.Input(SecretChatTestHarness.AdminId,
                SecretChatTestHarness.AdminPermAuthKeyId);

            _service = new SecretChatAppService(CommandBus,
                QueryProcessor,
                new FakeIdGenerator(),
                BlockCache,
                new SecretChatAccessResolver(QueryProcessor),
                Dispatcher,
                new InMemorySecretChatMessageStore(),
                Ledger,
                new InMemoryEncryptedFileStore(),
                SecretChatTestHarness.ChatConverters(),
                SecretChatTestHarness.MessageConverters(),
                SecretChatTestHarness.FileConverters());
        }

        public FakeQueryProcessor QueryProcessor { get; }
        public RecordingCommandBus CommandBus { get; }
        public RecordingUpdateDispatcher Dispatcher { get; }
        public InMemorySecretChatRequestLedger Ledger { get; }
        public CountingBlockCacheAppService BlockCache { get; }

        public IEncryptedChat Invoke()
        {
            return _service.RequestEncryptionAsync(_input, _targetInputUser, _randomId, _ga)
                .GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// One fully wired sendEncryptedService scenario: the REAL service + REAL access resolver over the
    /// harness fakes, with the case's chat state and participant state installed.
    /// </summary>
    private sealed class ServiceWorld
    {
        private readonly SecretChatAppService _service;
        private readonly TestRequestInput _input;
        private readonly SecretChatServiceFailureCase _case;

        public ServiceWorld(SecretChatServiceFailureCase @case)
        {
            _case = @case;
            QueryProcessor = new FakeQueryProcessor();
            CommandBus = new RecordingCommandBus();
            Dispatcher = new RecordingUpdateDispatcher();
            MessageStore = new InMemorySecretChatMessageStore();

            var chat = SecretChatTestHarness.Chat(@case.State);
            QueryProcessor.Chats[chat.ChatId] = chat;

            OtherUserId = @case.CallerIsAdmin ? SecretChatTestHarness.ParticipantId : SecretChatTestHarness.AdminId;
            OtherPermAuthKeyId = @case.CallerIsAdmin
                ? SecretChatTestHarness.ParticipantPermAuthKeyId
                : SecretChatTestHarness.AdminPermAuthKeyId;

            var callerUserId = @case.CallerIsAdmin ? SecretChatTestHarness.AdminId : SecretChatTestHarness.ParticipantId;
            var callerPermAuthKeyId = @case.CallerIsAdmin
                ? SecretChatTestHarness.AdminPermAuthKeyId
                : SecretChatTestHarness.ParticipantPermAuthKeyId;

            QueryProcessor.Users[callerUserId] = FakeUser.Create(callerUserId);
            QueryProcessor.Users[OtherUserId] = FakeUser.Create(OtherUserId,
                isDeleted: @case.OtherParticipantDeleted ? true : null);

            _input = SecretChatTestHarness.Input(callerUserId, callerPermAuthKeyId);

            _service = new SecretChatAppService(CommandBus,
                QueryProcessor,
                new FakeIdGenerator(),
                new FakeBlockCacheAppService(),
                new SecretChatAccessResolver(QueryProcessor),
                Dispatcher,
                MessageStore,
                new InMemorySecretChatRequestLedger(),
                new InMemoryEncryptedFileStore(),
                SecretChatTestHarness.ChatConverters(),
                SecretChatTestHarness.MessageConverters(),
                SecretChatTestHarness.FileConverters());
        }

        public FakeQueryProcessor QueryProcessor { get; }
        public RecordingCommandBus CommandBus { get; }
        public RecordingUpdateDispatcher Dispatcher { get; }
        public InMemorySecretChatMessageStore MessageStore { get; }
        public long OtherUserId { get; }
        public long OtherPermAuthKeyId { get; }

        public MyTelegram.Schema.Messages.ISentEncryptedMessage Invoke()
        {
            return _service
                .SendEncryptedServiceAsync(_input, SecretChatTestHarness.InputChat(), _case.RandomId, _case.Data)
                .GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Block cache that both answers from an explicit relation set and counts how often it was consulted,
    /// so "the block check never ran" can be asserted structurally instead of inferred from the error.
    /// Only <see cref="IsBlockedAsync"/> is exercised by requestEncryption; the mutating members throw so
    /// an unexpected call cannot pass silently.
    /// </summary>
    private sealed class CountingBlockCacheAppService : IBlockCacheAppService
    {
        public HashSet<(long BlockerId, long BlockedId)> Blocks { get; } = [];

        public int IsBlockedCallCount { get; private set; }

        public Task<bool> IsBlockedAsync(long userId, long targetPeerId)
        {
            IsBlockedCallCount++;

            return Task.FromResult(Blocks.Contains((userId, targetPeerId)));
        }

        public Task BlockAsync(long userId, long targetPeerId, PeerType targetPeerType = PeerType.User,
            bool myStoriesFrom = false) => throw new NotSupportedException();

        public Task<BlockedPeerCachePage> GetBlockedAsync(long userId, int offset, int limit,
            bool myStoriesFrom = false) => throw new NotSupportedException();

        public Task UnblockAsync(long userId, long targetPeerId, PeerType targetPeerType = PeerType.User,
            bool myStoriesFrom = false) => throw new NotSupportedException();

        public Task ReplaceBlockedAsync(long userId, IReadOnlyCollection<Peer> peers, bool myStoriesFrom = false)
            => throw new NotSupportedException();
    }
}

/// <summary>
/// What the <c>InputUser</c> of a generated requestEncryption case points at. Only
/// <see cref="Existing"/> resolves; the other four are the distinct ways target resolution fails, all of
/// which must surface as USER_ID_INVALID.
/// </summary>
public enum SecretChatRequestTargetKind
{
    /// <summary>A registered, non-bot user — target resolution succeeds.</summary>
    Existing,

    /// <summary>An <c>inputUser</c> whose id has no read model.</summary>
    Missing,

    /// <summary>An <c>inputUser</c> carrying the caller's own id.</summary>
    SelfById,

    /// <summary>An <c>inputUserSelf</c> (the service resolves it to id 0).</summary>
    InputUserSelf,

    /// <summary>A registered user flagged as a bot.</summary>
    Bot
}

/// <summary>
/// One generated requestEncryption case. Every condition is toggled independently of the others so the
/// generator produces simultaneous violations, which is what makes the precedence observable.
/// </summary>
public sealed record SecretChatRequestFailureCase(SecretChatRequestTargetKind Target,
    bool TargetIsDeleted,
    bool TargetBlockedCaller,
    bool GaValid,
    int RandomId)
{
    /// <summary>
    /// The out-of-range g_a used when <see cref="GaValid"/> is false. Defaults to the empty value; the
    /// generator replaces it with the other invalid shapes (too small, zero).
    /// </summary>
    public byte[] InvalidGa { get; init; } = [];
}

/// <summary>
/// One generated sendEncryptedService case: the stored chat state, whether the other participant is
/// deleted, which side sends, and the payload parameters.
/// </summary>
public sealed record SecretChatServiceFailureCase(ChatState State,
    bool OtherParticipantDeleted,
    bool CallerIsAdmin,
    long RandomId,
    byte[] Data);

/// <summary>
/// FsCheck generators for Properties 17 and 18. Only the two case records get a custom arbitrary; every
/// field is drawn from an explicit <c>Gen</c>, so no primitive arbitrary is re-registered onto itself.
/// </summary>
public static class SecretChatFailureOrderArbitraries
{
    public static Arbitrary<SecretChatRequestFailureCase> RequestFailureCase() => Arb.From(RequestCaseGen);

    public static Arbitrary<SecretChatServiceFailureCase> ServiceFailureCase() => Arb.From(ServiceCaseGen);

    // ---- Property 17 ---------------------------------------------------------------------------

    private static Gen<SecretChatRequestTargetKind> TargetKind =>
        Gen.Elements(SecretChatRequestTargetKind.Existing,
            SecretChatRequestTargetKind.Missing,
            SecretChatRequestTargetKind.SelfById,
            SecretChatRequestTargetKind.InputUserSelf,
            SecretChatRequestTargetKind.Bot);

    /// <summary>
    /// The three shapes of an out-of-range g_a: empty (rejected outright), a value far below the
    /// 2^(2048-64) safety bound, and an all-zero 256-byte value (g == 0).
    /// </summary>
    private static Gen<byte[]> InvalidGaGen =>
        Gen.Frequency(Tuple.Create(1, Gen.Constant(Array.Empty<byte>())),
            Tuple.Create(1, Gen.Choose(1, 8).Select(BuildSmallValue)),
            Tuple.Create(1, Gen.Constant(new byte[256])));

    private static Gen<SecretChatRequestFailureCase> RequestCaseGen =>
        from target in TargetKind
        from targetIsDeleted in Gen.Elements(true, false)
        from blocked in Gen.Elements(true, false)
        from gaValid in Gen.Elements(true, false)
        from randomId in Gen.Choose(1, 1_000_000)
        from invalidGa in InvalidGaGen
        select new SecretChatRequestFailureCase(target, targetIsDeleted, blocked, gaValid, randomId)
        {
            InvalidGa = invalidGa
        };

    // ---- Property 18 ---------------------------------------------------------------------------

    /// <summary>Only Waiting/Active/Discarded are ever persisted; Requested is a converter-only view.</summary>
    private static Gen<ChatState> StoredState =>
        Gen.Elements(ChatState.Waiting, ChatState.Active, ChatState.Discarded);

    private static Gen<SecretChatServiceFailureCase> ServiceCaseGen =>
        from state in StoredState
        from otherDeleted in Gen.Elements(true, false)
        from callerIsAdmin in Gen.Elements(true, false)
        from randomId in Gen.Choose(1, 1_000_000)
        from data in Payload
        select new SecretChatServiceFailureCase(state, otherDeleted, callerIsAdmin, randomId, data);

    /// <summary>
    /// Straddles <see cref="SecretChatConsts.MinEncryptedPayloadLength"/> (40) on both sides, so the
    /// draws exercise the DATA_INVALID branch and the delivered branch alike.
    /// </summary>
    private static Gen<byte[]> Payload =>
        from length in Gen.Choose(1, 96)
        from seed in Gen.Choose(0, 255)
        select BuildPayload(length, seed);

    private static byte[] BuildSmallValue(int length)
    {
        // A short big-endian value: numerically far below the 2^(2048-64) DH safety bound.
        var value = new byte[length];
        for (var i = 0; i < length; i++)
        {
            value[i] = (byte)(i + 3);
        }

        return value;
    }

    private static byte[] BuildPayload(int length, int seed)
    {
        var payload = new byte[length];
        for (var i = 0; i < length; i++)
        {
            payload[i] = (byte)((seed + i * 31) % 256);
        }

        return payload;
    }
}
