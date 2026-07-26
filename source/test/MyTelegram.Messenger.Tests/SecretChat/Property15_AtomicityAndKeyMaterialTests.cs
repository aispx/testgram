using System.Numerics;
using FsCheck;
using FsCheck.Xunit;
using MyTelegram;
using MyTelegram.Core;
using MyTelegram.Domain.Aggregates.EncryptedChat;
using MyTelegram.Messenger.Services.SecretChat;
using MyTelegram.Schema;
using SchemaMessages = MyTelegram.Schema.Messages;

namespace MyTelegram.Messenger.Tests.SecretChat;

/// <summary>
/// Feature: secret-chats, Property 15: Failure atomicity — and Property 16: Required key-exchange values
/// are present.
///
/// Property 15: for any request that must persist <c>g_a</c> / <c>g_b</c> / <c>key_fingerprint</c> / an
/// Encrypted_Blob, or project domain events, if that persistence or projection is impossible the request
/// fails with an error, the chat stays in the Chat_State it held before the request, and no result
/// implying a successful mutation is returned.
///
/// Validates: Requirements 16.6, 17.7.
///
/// Property 16: for any request required to carry <c>g_a</c>, <c>g_b</c> or a <c>key_fingerprint</c>, if
/// the value is missing or empty the request is rejected with an error and the chat does not move to a
/// state implying the value was supplied.
///
/// Validates: Requirements 16.8.
///
/// How Property 15 is tested: <see cref="SecretChatAtomicityCase"/> generates the operation
/// (requestEncryption / acceptEncryption / discardEncryption — whose mutations go through the aggregate
/// command bus — and sendEncrypted / sendEncryptedService — whose mutation is the blob insert), a flag
/// deciding whether the fault is injected at all, the kind of infrastructure exception to inject
/// (InvalidOperationException / TimeoutException / IOException — the failure mode must not matter), which
/// side of the chat calls, the delete_history flag and the operation's nuisance parameters (random_ids,
/// payload bytes, key_fingerprint, silent). Each case drives the REAL <see cref="SecretChatAppService"/>
/// wired to the REAL <see cref="SecretChatAccessResolver"/>; the fault is injected through the harness
/// hooks <see cref="RecordingCommandBus.ThrowOnPublish"/> (event persistence / projection impossible) and
/// <see cref="InMemorySecretChatMessageStore.ThrowOnStore"/> (blob persistence impossible).
///
/// The expectation is computed independently of the production code, from the case alone: when a fault is
/// injected the invocation must surface THAT EXACT exception instance (no swallowing, no conversion into a
/// success), must produce NO return value at all, must leave the dispatcher empty (no update claiming the
/// mutation happened), must leave the command log empty, must not have inserted a blob, must not have
/// burned a qts on either device (a burned qts would punch a permanent gap in the recipient's sequence),
/// must not have run the destructive delete_history projection (a blob seeded before the discard is still
/// present), and must leave the stored chat's Chat_State exactly as it was before the request. When no
/// fault is injected the same case must complete: it returns the schema object, publishes exactly the one
/// expected aggregate command and delivers exactly the documented fan-out. Comparing the faulted and
/// non-faulted runs of the SAME generated case is what makes "the failure changed nothing" meaningful.
///
/// Known gap (reported, not asserted): requestEncryption reserves the (admin, random_id) ledger row BEFORE
/// publishing CreateEncryptedChatCommand, so a command-bus failure leaves an orphan reservation behind.
/// The failing request itself still satisfies this property — it errors, creates no chat, delivers no
/// update — but a later retry with the same random_id short-circuits on that reservation and returns
/// encryptedChatWaiting for a chat that was never created. That is a defect of the RETRY, outside the
/// scope of the property asserted here, and is left to the production fix rather than pinned by a test.
///
/// How Property 16 is tested: <see cref="SecretChatKeyMaterialCase"/> generates the operation carrying the
/// DH value (requestEncryption carries <c>g_a</c>, acceptEncryption carries <c>g_b</c>) and the SHAPE of
/// that value — absent (<c>null</c>), empty (zero-length), a single zero byte, 256 zero bytes, the
/// numerically-too-small values 1 and 2, 248 bytes of 0xFF and a 256-byte array padded with leading zeros
/// (both below the 2^(2048-64) safety bound), <c>p - 2^(2048-64) + 1</c> (above the upper safety bound),
/// <c>p - 1</c> and <c>p</c> (at/above the plain upper bound) — contrasted with three shapes that MUST be
/// accepted: <see cref="SecretChatTestHarness.ValidDhValue"/> and the two inclusive safety bounds
/// 2^(2048-64) and <c>p - 2^(2048-64)</c>. The oracle (<see cref="SecretChatKeyMaterialGen.IsAcceptable"/>)
/// classifies the shape from the published DH bounds alone, never by calling the production validator.
/// A rejected request must raise DH_GA_INVALID, publish no command, dispatch no update, store no blob and
/// leave the chat in the exact Chat_State it had (Waiting stays Waiting — it never moves to Active, which
/// would imply <c>g_b</c> had been supplied). An accepted request must relay the supplied value
/// byte-for-byte into both the aggregate command and the update. A companion property pins the
/// <c>key_fingerprint</c> half: the field is a non-optional 64-bit TL value with no "absent" encoding, so
/// absence is unrepresentable past deserialization; every supplied value — including 0, -1,
/// long.MinValue and long.MaxValue — is therefore relayed bit-for-bit into the command and the update
/// rather than being reinterpreted as missing. Each property runs a minimum of 100 generated cases.
/// </summary>
public class Property15_AtomicityAndKeyMaterialTests
{
    /// <summary>random_id of the blob seeded before a discardEncryption case, to observe delete_history.</summary>
    private const long SeededRandomId = 909_090;

    #region Property 15 — failure atomicity

    // 400 draws over a 5-operation x 2-fault x 3-exception x 2-caller-side space so every combination
    // of operation, fault mode and calling side is hit.
    [Property(Arbitrary = new[] { typeof(SecretChatAtomicityArbitraries) }, MaxTest = 400)]
    public void A_failed_persistence_or_projection_leaves_the_chat_untouched(SecretChatAtomicityCase @case)
    {
        var world = new SecretChatAtomicityWorld(@case);
        var stateBefore = world.Chat?.ChatState;

        object? result = null;
        var thrown = Capture(() => result = world.Invoke());

        if (!@case.InjectFault)
        {
            // ---- Control run: the very same case completes and mutates exactly once ---------------
            thrown.ShouldBeNull();
            result.ShouldNotBeNull();
            AssertSuccessfulOutcome(world, @case, result!);

            return;
        }

        // ---- Faulted run: the infrastructure exception surfaces unchanged ---------------------------
        thrown.ShouldNotBeNull();
        thrown.ShouldBeSameAs(world.Fault);

        // No result object at all — nothing that could imply the mutation succeeded (Req 17.7).
        result.ShouldBeNull();

        // No update was delivered claiming the chat/message changed (Req 16.6).
        world.Dispatcher.Dispatched.ShouldBeEmpty();

        // No aggregate command survived: the command bus is the ONLY path from the service to the
        // Encrypted_Chat_Store, so an empty log is the observable form of "no events were persisted".
        world.CommandBus.Published.ShouldBeEmpty();

        // The blob insert either never ran or threw: only the pre-seeded blob remains.
        world.MessageStore.All.Count.ShouldBe(world.SeededMessageCount);
        world.MessageStore.FindAsync(SecretChatTestHarness.ChatId, world.CallerUserId, @case.SendRandomId)
            .GetAwaiter().GetResult()
            .ShouldBeNull();

        // No qts was burned on either device — a failed send must not leave a hole in the sequence.
        world.HighestQts(SecretChatTestHarness.AdminId, SecretChatTestHarness.AdminPermAuthKeyId)
            .ShouldBe(SecretChatConsts.QtsInitialValue - 1);
        world.HighestQts(SecretChatTestHarness.ParticipantId, SecretChatTestHarness.ParticipantPermAuthKeyId)
            .ShouldBe(SecretChatConsts.QtsInitialValue - 1);

        // The chat is left in exactly the Chat_State it held before the request (Req 16.6).
        world.Chat?.ChatState.ShouldBe(stateBefore!.Value);
    }

    /// <summary>
    /// The destructive half of Requirement 16.6 for discardEncryption(delete_history = true): when the
    /// aggregate command cannot be published, the server-side history deletion must not run either — the
    /// request must be all-or-nothing rather than "history gone, chat still active".
    /// </summary>
    [Property(Arbitrary = new[] { typeof(SecretChatAtomicityArbitraries) }, MaxTest = 100)]
    public void A_failed_discard_does_not_delete_history(SecretChatAtomicityCase @case)
    {
        // Force the discard shape of the case; everything else stays as generated.
        var discardCase = @case with
        {
            Operation = SecretChatFaultOperation.DiscardEncryption,
            DeleteHistory = true,
            InjectFault = true
        };

        var world = new SecretChatAtomicityWorld(discardCase);
        world.MessageStore.All.Count.ShouldBe(1, "the discard cases seed one stored blob");

        var thrown = Capture(() => world.Invoke());

        thrown.ShouldBeSameAs(world.Fault);

        // delete_history runs strictly after the command: a failed command leaves the blob in place.
        world.MessageStore.All.Count.ShouldBe(1);
        world.MessageStore
            .FindAsync(SecretChatTestHarness.ChatId, SecretChatTestHarness.AdminId, SeededRandomId)
            .GetAwaiter().GetResult()
            .ShouldNotBeNull();

        // ...and the same case WITHOUT the fault does delete it, proving the assertion above is not vacuous.
        var successWorld = new SecretChatAtomicityWorld(discardCase with { InjectFault = false });
        successWorld.Invoke().ShouldBeOfType<TBoolTrue>();
        successWorld.MessageStore.All.ShouldBeEmpty();
    }

    /// <summary>
    /// The outcome the case must produce when no fault is injected: the schema result object, exactly one
    /// aggregate command of the expected type (none for the send operations, which write only the blob),
    /// and the operation's documented fan-out.
    /// </summary>
    private static void AssertSuccessfulOutcome(SecretChatAtomicityWorld world,
        SecretChatAtomicityCase @case,
        object result)
    {
        switch (@case.Operation)
        {
            case SecretChatFaultOperation.RequestEncryption:
                result.ShouldBeOfType<TEncryptedChatWaiting>();
                world.CommandBus.Published.ShouldHaveSingleItem().ShouldBeOfType<CreateEncryptedChatCommand>();
                world.Dispatcher.Dispatched.Count.ShouldBe(1);
                break;

            case SecretChatFaultOperation.AcceptEncryption:
                result.ShouldBeOfType<TEncryptedChat>();
                world.CommandBus.Published.ShouldHaveSingleItem().ShouldBeOfType<AcceptEncryptedChatCommand>();
                // encryptedChat to the admin's bound device + encryptedChatDiscarded to the other devices.
                world.Dispatcher.Dispatched.Count.ShouldBe(2);
                break;

            case SecretChatFaultOperation.DiscardEncryption:
                result.ShouldBeOfType<TBoolTrue>();
                world.CommandBus.Published.ShouldHaveSingleItem().ShouldBeOfType<DiscardEncryptedChatCommand>();
                // encryptedChatDiscarded to the other party + to the caller's other devices.
                world.Dispatcher.Dispatched.Count.ShouldBe(2);
                world.MessageStore.All.Count.ShouldBe(@case.DeleteHistory ? 0 : 1);
                break;

            case SecretChatFaultOperation.SendEncrypted:
            case SecretChatFaultOperation.SendEncryptedService:
                result.ShouldBeOfType<SchemaMessages.TSentEncryptedMessage>();
                // Sends never touch the aggregate; the blob store is the persistence boundary.
                world.CommandBus.Published.ShouldBeEmpty();
                world.MessageStore.All.Count.ShouldBe(1);
                world.Dispatcher.Dispatched.Count.ShouldBe(1);
                world.HighestQts(world.OtherUserId, world.OtherPermAuthKeyId)
                    .ShouldBe(SecretChatConsts.QtsInitialValue);
                break;

            default:
                throw new NotSupportedException($"Unexpected operation {@case.Operation}");
        }
    }

    #endregion

    #region Property 16 — required key-exchange values must be present

    // 300 draws over a 2-operation x 14-shape space so every (operation, shape) pair is hit.
    [Property(Arbitrary = new[] { typeof(SecretChatKeyMaterialArbitraries) }, MaxTest = 300)]
    public void A_missing_or_out_of_range_dh_value_is_rejected_and_moves_no_state(SecretChatKeyMaterialCase @case)
    {
        var world = new SecretChatKeyMaterialWorld(@case.Operation);
        var value = SecretChatKeyMaterialGen.Materialize(@case.Shape);
        var stateBefore = world.Chat?.ChatState;

        object? result = null;
        var thrown = Capture(() => result = world.Invoke(value, @case.RequestRandomId, @case.KeyFingerprint));

        if (!SecretChatKeyMaterialGen.IsAcceptable(@case.Shape))
        {
            // ---- Absent / empty / out-of-range: rejected ------------------------------------------
            var ex = thrown.ShouldBeOfType<RpcException>();
            ex.RpcError.ShouldBe(RpcErrors.RpcErrors400.DhGAInvalid);

            result.ShouldBeNull();

            // The chat does not move to a state implying the value was supplied.
            world.CommandBus.Published.ShouldBeEmpty();
            world.Dispatcher.Dispatched.ShouldBeEmpty();
            world.MessageStore.All.ShouldBeEmpty();
            world.Chat?.ChatState.ShouldBe(stateBefore!.Value);

            return;
        }

        // ---- Present and in range: accepted, and relayed byte-for-byte -----------------------------
        thrown.ShouldBeNull();
        result.ShouldNotBeNull();

        if (@case.Operation == SecretChatKeyMaterialOperation.RequestEncryption)
        {
            var command = world.CommandBus.Published.ShouldHaveSingleItem()
                .ShouldBeOfType<CreateEncryptedChatCommand>();
            command.Ga.ShouldBe(value);

            var requested = world.Dispatcher.Dispatched.ShouldHaveSingleItem().Update
                .ShouldBeOfType<TUpdateEncryption>().Chat
                .ShouldBeOfType<TEncryptedChatRequested>();
            requested.GA.ShouldBe(value);
        }
        else
        {
            var command = world.CommandBus.Published.ShouldHaveSingleItem()
                .ShouldBeOfType<AcceptEncryptedChatCommand>();
            command.Gb.ShouldBe(value);
            command.KeyFingerprint.ShouldBe(@case.KeyFingerprint);

            var forAdmin = world.Dispatcher.Dispatched[0].Update
                .ShouldBeOfType<TUpdateEncryption>().Chat
                .ShouldBeOfType<TEncryptedChat>();
            forAdmin.GAOrB.ShouldBe(value);
            forAdmin.KeyFingerprint.ShouldBe(@case.KeyFingerprint);
        }
    }

    /// <summary>
    /// The <c>key_fingerprint</c> half of Requirement 16.8. <c>key_fingerprint</c> is a non-optional 64-bit
    /// TL field: a request that omits it fails to deserialize and never reaches the handler, so "absent" is
    /// unrepresentable at this layer. What the service must therefore guarantee is that no supplied
    /// value — including 0, -1, long.MinValue and long.MaxValue — is reinterpreted as "missing": every one
    /// is relayed bit-for-bit into the aggregate command and into the update the Requester receives.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(SecretChatKeyMaterialArbitraries) }, MaxTest = 200)]
    public void Every_supplied_key_fingerprint_is_relayed_bit_for_bit(SecretChatKeyFingerprintCase @case)
    {
        var world = new SecretChatKeyMaterialWorld(SecretChatKeyMaterialOperation.AcceptEncryption);
        var gb = SecretChatTestHarness.ValidDhValue();

        var accepted = world.Invoke(gb, randomId: 1, keyFingerprint: @case.KeyFingerprint)
            .ShouldBeOfType<TEncryptedChat>();

        // The value the accepting Participant supplied reaches the aggregate unchanged...
        var command = world.CommandBus.Published.ShouldHaveSingleItem()
            .ShouldBeOfType<AcceptEncryptedChatCommand>();
        command.KeyFingerprint.ShouldBe(@case.KeyFingerprint);

        // ...the Requester's update carries the identical 64 bits...
        world.Dispatcher.Dispatched[0].Update.ShouldBeOfType<TUpdateEncryption>().Chat
            .ShouldBeOfType<TEncryptedChat>()
            .KeyFingerprint.ShouldBe(@case.KeyFingerprint);

        // ...and so does the object returned to the caller.
        accepted.KeyFingerprint.ShouldBe(@case.KeyFingerprint);
    }

    #endregion

    /// <summary>Runs <paramref name="action"/> and returns the exception it raised, or <c>null</c>.</summary>
    private static Exception? Capture(Action action)
    {
        try
        {
            action();

            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    // ---- Worlds ------------------------------------------------------------------------------------

    /// <summary>
    /// One fully wired secret-chat world for a Property 15 case: the REAL service and access resolver over
    /// the harness collaborators, with the fault armed on the collaborator the case's operation persists
    /// through.
    /// </summary>
    private sealed class SecretChatAtomicityWorld
    {
        private readonly SecretChatAtomicityCase _case;

        public SecretChatAtomicityWorld(SecretChatAtomicityCase @case)
        {
            _case = @case;
            Fault = BuildFault(@case.FaultKind);

            QueryProcessor.Users[SecretChatTestHarness.AdminId] =
                FakeUser.Create(SecretChatTestHarness.AdminId);
            QueryProcessor.Users[SecretChatTestHarness.ParticipantId] =
                FakeUser.Create(SecretChatTestHarness.ParticipantId);

            // requestEncryption creates the chat, so no stored chat exists for it; acceptEncryption needs
            // a Waiting chat; discard/send need an Active one.
            if (@case.Operation != SecretChatFaultOperation.RequestEncryption)
            {
                var state = @case.Operation == SecretChatFaultOperation.AcceptEncryption
                    ? ChatState.Waiting
                    : ChatState.Active;
                Chat = SecretChatTestHarness.Chat(state);
                QueryProcessor.Chats[Chat.ChatId] = Chat;
            }

            // acceptEncryption may only be issued by the Participant; the other operations take the
            // generated side of the chat.
            var callerIsAdmin = @case.Operation != SecretChatFaultOperation.AcceptEncryption &&
                                @case.CallerIsAdmin;
            CallerUserId = callerIsAdmin ? SecretChatTestHarness.AdminId : SecretChatTestHarness.ParticipantId;
            CallerPermAuthKeyId = callerIsAdmin
                ? SecretChatTestHarness.AdminPermAuthKeyId
                : SecretChatTestHarness.ParticipantPermAuthKeyId;
            OtherUserId = callerIsAdmin ? SecretChatTestHarness.ParticipantId : SecretChatTestHarness.AdminId;
            OtherPermAuthKeyId = callerIsAdmin
                ? SecretChatTestHarness.ParticipantPermAuthKeyId
                : SecretChatTestHarness.AdminPermAuthKeyId;

            // A blob seeded before the discard makes the destructive delete_history projection observable.
            if (@case.Operation == SecretChatFaultOperation.DiscardEncryption)
            {
                SeedMessage();
                SeededMessageCount = 1;
            }

            if (@case.InjectFault)
            {
                if (IsCommandBusOperation(@case.Operation))
                {
                    // Domain events cannot be persisted/projected (Req 17.7).
                    CommandBus.ThrowOnPublish = Fault;
                }
                else
                {
                    // The Encrypted_Blob cannot be persisted (Req 16.6).
                    MessageStore.ThrowOnStore = Fault;
                }
            }

            Input = SecretChatTestHarness.Input(CallerUserId, CallerPermAuthKeyId);
            Service = new SecretChatAppService(CommandBus,
                QueryProcessor,
                new FakeIdGenerator(),
                new FakeBlockCacheAppService(),
                new SecretChatAccessResolver(QueryProcessor),
                Dispatcher,
                MessageStore,
                new InMemorySecretChatRequestLedger(),
                FileStore,
                SecretChatTestHarness.ChatConverters(),
                SecretChatTestHarness.MessageConverters(),
                SecretChatTestHarness.FileConverters());
        }

        public Exception Fault { get; }
        public FakeEncryptedChatReadModel? Chat { get; }
        public int SeededMessageCount { get; }
        public long CallerUserId { get; }
        public long CallerPermAuthKeyId { get; }
        public long OtherUserId { get; }
        public long OtherPermAuthKeyId { get; }
        public FakeQueryProcessor QueryProcessor { get; } = new();
        public RecordingCommandBus CommandBus { get; } = new();
        public RecordingUpdateDispatcher Dispatcher { get; } = new();
        public InMemorySecretChatMessageStore MessageStore { get; } = new();
        public InMemoryEncryptedFileStore FileStore { get; } = new();
        public SecretChatAppService Service { get; }
        public TestRequestInput Input { get; }

        public int HighestQts(long userId, long permAuthKeyId)
        {
            return MessageStore.GetHighestQtsAsync(userId, permAuthKeyId).GetAwaiter().GetResult();
        }

        public object Invoke()
        {
            var peer = SecretChatTestHarness.InputChat();

            return _case.Operation switch
            {
                // The Requester is the calling side; the target is the other user.
                SecretChatFaultOperation.RequestEncryption => Service.RequestEncryptionAsync(Input,
                        new TInputUser { UserId = OtherUserId, AccessHash = 0 },
                        _case.RequestRandomId,
                        SecretChatTestHarness.ValidDhValue())
                    .GetAwaiter().GetResult(),
                SecretChatFaultOperation.AcceptEncryption => Service.AcceptEncryptionAsync(Input,
                        peer,
                        SecretChatTestHarness.ValidDhValue(),
                        _case.KeyFingerprint)
                    .GetAwaiter().GetResult(),
                SecretChatFaultOperation.DiscardEncryption => Service
                    .DiscardEncryptionAsync(Input, SecretChatTestHarness.ChatId, _case.DeleteHistory)
                    .GetAwaiter().GetResult(),
                SecretChatFaultOperation.SendEncrypted => Service
                    .SendEncryptedAsync(Input, peer, _case.SendRandomId, _case.Data, _case.Silent)
                    .GetAwaiter().GetResult(),
                SecretChatFaultOperation.SendEncryptedService => Service
                    .SendEncryptedServiceAsync(Input, peer, _case.SendRandomId, _case.Data)
                    .GetAwaiter().GetResult(),
                _ => throw new NotSupportedException($"Unexpected operation {_case.Operation}")
            };
        }

        private void SeedMessage()
        {
            MessageStore.StoreAsync(new EncryptedMessageDocument
                {
                    Id = EncryptedMessageDocument.BuildId(SecretChatTestHarness.ChatId,
                        SecretChatTestHarness.AdminId,
                        SeededRandomId),
                    ChatId = SecretChatTestHarness.ChatId,
                    UserId = SecretChatTestHarness.AdminId,
                    PermAuthKeyId = SecretChatTestHarness.AdminPermAuthKeyId,
                    RecipientUserId = SecretChatTestHarness.ParticipantId,
                    RecipientPermAuthKeyId = SecretChatTestHarness.ParticipantPermAuthKeyId,
                    Data = [1, 2, 3, 4],
                    Date = 1000,
                    MessageType = SendMessageType.Text,
                    RandomId = SeededRandomId
                })
                .GetAwaiter().GetResult();
        }

        private static bool IsCommandBusOperation(SecretChatFaultOperation operation)
        {
            return operation is SecretChatFaultOperation.RequestEncryption
                or SecretChatFaultOperation.AcceptEncryption
                or SecretChatFaultOperation.DiscardEncryption;
        }

        private static Exception BuildFault(SecretChatFaultExceptionKind kind)
        {
            return kind switch
            {
                SecretChatFaultExceptionKind.InvalidOperation =>
                    new InvalidOperationException("event store unavailable"),
                SecretChatFaultExceptionKind.Timeout => new TimeoutException("write timed out"),
                SecretChatFaultExceptionKind.Io => new IOException("storage node unreachable"),
                _ => throw new NotSupportedException($"Unexpected fault kind {kind}")
            };
        }
    }

    /// <summary>
    /// One fully wired secret-chat world for a Property 16 case: requestEncryption is issued by the admin
    /// against the participant, acceptEncryption by the participant against a Waiting chat.
    /// </summary>
    private sealed class SecretChatKeyMaterialWorld
    {
        private readonly SecretChatKeyMaterialOperation _operation;

        public SecretChatKeyMaterialWorld(SecretChatKeyMaterialOperation operation)
        {
            _operation = operation;

            QueryProcessor.Users[SecretChatTestHarness.AdminId] =
                FakeUser.Create(SecretChatTestHarness.AdminId);
            QueryProcessor.Users[SecretChatTestHarness.ParticipantId] =
                FakeUser.Create(SecretChatTestHarness.ParticipantId);

            if (operation == SecretChatKeyMaterialOperation.AcceptEncryption)
            {
                Chat = SecretChatTestHarness.Chat(ChatState.Waiting);
                QueryProcessor.Chats[Chat.ChatId] = Chat;
            }

            var callerUserId = operation == SecretChatKeyMaterialOperation.RequestEncryption
                ? SecretChatTestHarness.AdminId
                : SecretChatTestHarness.ParticipantId;
            var callerPermAuthKeyId = operation == SecretChatKeyMaterialOperation.RequestEncryption
                ? SecretChatTestHarness.AdminPermAuthKeyId
                : SecretChatTestHarness.ParticipantPermAuthKeyId;

            Input = SecretChatTestHarness.Input(callerUserId, callerPermAuthKeyId);
            Service = new SecretChatAppService(CommandBus,
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

        public FakeEncryptedChatReadModel? Chat { get; }
        public FakeQueryProcessor QueryProcessor { get; } = new();
        public RecordingCommandBus CommandBus { get; } = new();
        public RecordingUpdateDispatcher Dispatcher { get; } = new();
        public InMemorySecretChatMessageStore MessageStore { get; } = new();
        public SecretChatAppService Service { get; }
        public TestRequestInput Input { get; }

        public object Invoke(byte[]? dhValue, int randomId, long keyFingerprint)
        {
            return _operation == SecretChatKeyMaterialOperation.RequestEncryption
                ? Service.RequestEncryptionAsync(Input,
                        new TInputUser { UserId = SecretChatTestHarness.ParticipantId, AccessHash = 0 },
                        randomId,
                        dhValue!)
                    .GetAwaiter().GetResult()
                : Service.AcceptEncryptionAsync(Input, SecretChatTestHarness.InputChat(), dhValue!,
                        keyFingerprint)
                    .GetAwaiter().GetResult();
        }
    }
}

// ---- Property 15 case model ------------------------------------------------------------------------

/// <summary>The five operations whose effect must be all-or-nothing when persistence fails.</summary>
public enum SecretChatFaultOperation
{
    /// <summary>Persists the chat-created event through the command bus.</summary>
    RequestEncryption,

    /// <summary>Persists g_b + key_fingerprint through the command bus.</summary>
    AcceptEncryption,

    /// <summary>Persists the discard event through the command bus (and may delete history).</summary>
    DiscardEncryption,

    /// <summary>Persists the Encrypted_Blob through the message store.</summary>
    SendEncrypted,

    /// <summary>Persists the service Encrypted_Blob through the message store.</summary>
    SendEncryptedService
}

/// <summary>The infrastructure failure mode injected; the outcome must not depend on which one it is.</summary>
public enum SecretChatFaultExceptionKind
{
    InvalidOperation,
    Timeout,
    Io
}

/// <summary>
/// One generated failure-atomicity case: the operation, whether the persistence fault is armed, the kind
/// of infrastructure exception to raise, which side of the chat calls, and the operation's parameters.
/// </summary>
public sealed record SecretChatAtomicityCase(SecretChatFaultOperation Operation,
    bool InjectFault,
    SecretChatFaultExceptionKind FaultKind,
    bool CallerIsAdmin,
    bool DeleteHistory,
    int RequestRandomId,
    long SendRandomId,
    byte[] Data,
    long KeyFingerprint,
    bool Silent);

/// <summary>
/// FsCheck generators for Property 15. Only the case record gets a custom generator; every field is drawn
/// from an explicit <c>Gen</c> so no primitive arbitrary is re-registered onto itself.
/// </summary>
public static class SecretChatAtomicityArbitraries
{
    public static Arbitrary<SecretChatAtomicityCase> AtomicityCase() => Arb.From(CaseGen);

    private static Gen<SecretChatFaultOperation> Operation =>
        Gen.Elements(SecretChatFaultOperation.RequestEncryption,
            SecretChatFaultOperation.AcceptEncryption,
            SecretChatFaultOperation.DiscardEncryption,
            SecretChatFaultOperation.SendEncrypted,
            SecretChatFaultOperation.SendEncryptedService);

    private static Gen<SecretChatFaultExceptionKind> FaultKind =>
        Gen.Elements(SecretChatFaultExceptionKind.InvalidOperation,
            SecretChatFaultExceptionKind.Timeout,
            SecretChatFaultExceptionKind.Io);

    private static Gen<byte[]> Payload =>
        from length in Gen.Choose(1, 64)
        from seed in Gen.Choose(0, 255)
        select BuildPayload(length, seed);

    private static Gen<SecretChatAtomicityCase> CaseGen =>
        from operation in Operation
        from injectFault in Gen.Elements(true, false)
        from faultKind in FaultKind
        from callerIsAdmin in Gen.Elements(true, false)
        from deleteHistory in Gen.Elements(true, false)
        from requestRandomId in Gen.Choose(1, 1_000_000)
        from sendRandomId in Gen.Choose(1, 1_000_000).Select(i => (long)i)
        from data in Payload
        from keyFingerprint in Gen.Choose(1, int.MaxValue).Select(i => (long)i * 7919)
        from silent in Gen.Elements(true, false)
        select new SecretChatAtomicityCase(operation, injectFault, faultKind, callerIsAdmin, deleteHistory,
            requestRandomId, sendRandomId, data, keyFingerprint, silent);

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

// ---- Property 16 case model ------------------------------------------------------------------------

/// <summary>The two operations that must carry a DH value: g_a on request, g_b on accept.</summary>
public enum SecretChatKeyMaterialOperation
{
    RequestEncryption,
    AcceptEncryption
}

/// <summary>
/// The shape of the supplied DH value. The first group models an absent/empty/degenerate value, the
/// second group models values outside the documented range, and the last three are the values that MUST
/// be accepted (an ordinary in-range value and both inclusive safety bounds).
/// </summary>
public enum SecretChatDhValueShape
{
    /// <summary>The field was not supplied at all.</summary>
    Absent,

    /// <summary>Supplied but zero-length.</summary>
    Empty,

    /// <summary>A single 0x00 byte — numerically 0.</summary>
    SingleZeroByte,

    /// <summary>256 bytes of 0x00 — numerically 0 despite the plausible length.</summary>
    AllZero256,

    /// <summary>Numerically 1 — at the exclusive lower plain bound.</summary>
    One,

    /// <summary>Numerically 2 — above 1 but far below the 2^(2048-64) safety bound.</summary>
    Two,

    /// <summary>248 bytes of 0xFF — just below the 2^(2048-64) safety bound.</summary>
    JustBelowSafetyBound,

    /// <summary>256 bytes whose leading 16 bytes are zero — length looks right, value is too small.</summary>
    LeadingZeroPadded,

    /// <summary>p - 2^(2048-64) + 1 — one above the upper safety bound.</summary>
    JustAboveUpperSafetyBound,

    /// <summary>p - 1 — at the exclusive upper plain bound.</summary>
    PrimeMinusOne,

    /// <summary>p itself.</summary>
    Prime,

    /// <summary>An ordinary in-range value.</summary>
    Valid,

    /// <summary>Exactly 2^(2048-64) — the inclusive lower safety bound.</summary>
    LowerSafetyBound,

    /// <summary>Exactly p - 2^(2048-64) — the inclusive upper safety bound.</summary>
    UpperSafetyBound
}

/// <summary>One generated key-material case: which value the operation carries and in what shape.</summary>
public sealed record SecretChatKeyMaterialCase(SecretChatKeyMaterialOperation Operation,
    SecretChatDhValueShape Shape,
    int RequestRandomId,
    long KeyFingerprint);

/// <summary>One generated key_fingerprint, including the values a "missing" encoding might be confused with.</summary>
public sealed record SecretChatKeyFingerprintCase(long KeyFingerprint);

/// <summary>
/// Materializes the DH shapes from the published DH parameters and classifies them independently of the
/// production validator, so the oracle never mirrors the implementation.
/// </summary>
public static class SecretChatKeyMaterialGen
{
    /// <summary>The DH prime p shared with clients via messages.getDhConfig.</summary>
    private static readonly BigInteger Prime = AuthConsts.DhPrime;

    /// <summary>The documented safety bound 2^(2048-64).</summary>
    private static readonly BigInteger SafetyRange = BigInteger.One << (2048 - 64);

    public static byte[]? Materialize(SecretChatDhValueShape shape)
    {
        return shape switch
        {
            SecretChatDhValueShape.Absent => null,
            SecretChatDhValueShape.Empty => [],
            SecretChatDhValueShape.SingleZeroByte => [0x00],
            SecretChatDhValueShape.AllZero256 => new byte[256],
            SecretChatDhValueShape.One => [0x01],
            SecretChatDhValueShape.Two => [0x02],
            SecretChatDhValueShape.JustBelowSafetyBound => Repeat(0xFF, 248),
            SecretChatDhValueShape.LeadingZeroPadded => LeadingZeroPadded(),
            SecretChatDhValueShape.JustAboveUpperSafetyBound => ToBigEndian(Prime - SafetyRange + 1),
            SecretChatDhValueShape.PrimeMinusOne => ToBigEndian(Prime - 1),
            SecretChatDhValueShape.Prime => ToBigEndian(Prime),
            SecretChatDhValueShape.Valid => SecretChatTestHarness.ValidDhValue(),
            SecretChatDhValueShape.LowerSafetyBound => ToBigEndian(SafetyRange),
            SecretChatDhValueShape.UpperSafetyBound => ToBigEndian(Prime - SafetyRange),
            _ => throw new NotSupportedException($"Unexpected shape {shape}")
        };
    }

    /// <summary>
    /// The oracle: a DH value is acceptable exactly when it is present and lies in the documented range
    /// <c>1 &lt; g &lt; p - 1</c> intersected with <c>[2^(2048-64), p - 2^(2048-64)]</c>. Stated over the
    /// shape enum so the expectation is fixed by construction rather than recomputed by the validator.
    /// </summary>
    public static bool IsAcceptable(SecretChatDhValueShape shape)
    {
        return shape is SecretChatDhValueShape.Valid
            or SecretChatDhValueShape.LowerSafetyBound
            or SecretChatDhValueShape.UpperSafetyBound;
    }

    private static byte[] ToBigEndian(BigInteger value)
    {
        return value.ToByteArray(isUnsigned: true, isBigEndian: true);
    }

    private static byte[] Repeat(byte value, int length)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, value);

        return bytes;
    }

    /// <summary>256 bytes whose leading 16 bytes are zero: below 2^(2048-128) < 2^(2048-64).</summary>
    private static byte[] LeadingZeroPadded()
    {
        var bytes = new byte[256];
        for (var i = 16; i < bytes.Length; i++)
        {
            bytes[i] = 0xFF;
        }

        return bytes;
    }
}

/// <summary>
/// FsCheck generators for Property 16. Only the case records get custom generators; every field is drawn
/// from an explicit <c>Gen</c> so no primitive arbitrary is re-registered onto itself.
/// </summary>
public static class SecretChatKeyMaterialArbitraries
{
    public static Arbitrary<SecretChatKeyMaterialCase> KeyMaterialCase() => Arb.From(CaseGen);

    public static Arbitrary<SecretChatKeyFingerprintCase> KeyFingerprintCase() =>
        Arb.From(FingerprintGen.Select(f => new SecretChatKeyFingerprintCase(f)));

    private static Gen<SecretChatDhValueShape> Shape =>
        Gen.Elements(SecretChatDhValueShape.Absent,
            SecretChatDhValueShape.Empty,
            SecretChatDhValueShape.SingleZeroByte,
            SecretChatDhValueShape.AllZero256,
            SecretChatDhValueShape.One,
            SecretChatDhValueShape.Two,
            SecretChatDhValueShape.JustBelowSafetyBound,
            SecretChatDhValueShape.LeadingZeroPadded,
            SecretChatDhValueShape.JustAboveUpperSafetyBound,
            SecretChatDhValueShape.PrimeMinusOne,
            SecretChatDhValueShape.Prime,
            SecretChatDhValueShape.Valid,
            SecretChatDhValueShape.LowerSafetyBound,
            SecretChatDhValueShape.UpperSafetyBound);

    /// <summary>Edge fingerprints a naive "is it missing?" check might trip over, plus arbitrary ones.</summary>
    private static Gen<long> FingerprintGen =>
        Gen.Frequency(Tuple.Create(1,
                Gen.Elements(0L, 1L, -1L, long.MinValue, long.MaxValue, int.MinValue, (long)int.MaxValue)),
            Tuple.Create(1, ArbitraryLong));

    /// <summary>A full-width 64-bit value assembled from four 16-bit draws (no overflow-prone ranges).</summary>
    private static Gen<long> ArbitraryLong =>
        from a in Gen.Choose(0, 65535)
        from b in Gen.Choose(0, 65535)
        from c in Gen.Choose(0, 65535)
        from d in Gen.Choose(0, 65535)
        select unchecked((long)(((ulong)(uint)a << 48) | ((ulong)(uint)b << 32) | ((ulong)(uint)c << 16) |
                                (uint)d));

    private static Gen<SecretChatKeyMaterialCase> CaseGen =>
        from operation in Gen.Elements(SecretChatKeyMaterialOperation.RequestEncryption,
            SecretChatKeyMaterialOperation.AcceptEncryption)
        from shape in Shape
        from randomId in Gen.Choose(1, 1_000_000)
        from keyFingerprint in FingerprintGen
        select new SecretChatKeyMaterialCase(operation, shape, randomId, keyFingerprint);
}
