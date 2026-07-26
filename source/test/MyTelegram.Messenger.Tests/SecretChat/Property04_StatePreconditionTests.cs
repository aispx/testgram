using FsCheck;
using FsCheck.Xunit;
using MyTelegram;
using MyTelegram.Messenger.Services.SecretChat;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.SecretChat;

/// <summary>
/// Feature: secret-chats, Property 4: State preconditions for operations.
///
/// For any operation in {messages.sendEncrypted, messages.sendEncryptedFile,
/// messages.sendEncryptedService, messages.readEncryptedHistory, messages.setEncryptedTyping} and any
/// stored chat state, the operation proceeds only if the chat is Active. If the chat is Discarded the
/// operation errors — ENCRYPTION_DECLINED for the three send* operations, ENCRYPTION_ID_INVALID for
/// readEncryptedHistory/setEncryptedTyping. If the chat is Waiting the operation always errors with
/// ENCRYPTION_ID_INVALID. On any such error no blob is stored, no state changes and no update is
/// delivered.
///
/// Validates: Requirements 6.3, 6.6, 7.4, 8.3, 9.3, 9.4, 10.4, 10.5.
///
/// How this is tested: <see cref="SecretChatStatePreconditionCase"/> generates the full cross product of
/// the five operations and the three stored states (Waiting / Active / Discarded — "Requested" is a
/// converter view and is never stored), plus independent nuisance parameters (caller is the admin or the
/// participant, random_id, payload bytes, the typing flag, max_date) so the precondition is checked from
/// both sides of the chat and for arbitrary payloads. Each case drives the REAL
/// <see cref="SecretChatAppService"/> wired to the REAL <see cref="SecretChatAccessResolver"/> over the
/// in-memory harness collaborators; only the transport and stores are substituted. The expected outcome
/// is computed independently of the production code from the state/operation pair alone
/// (<see cref="ExpectedError"/>) and compared against what the service actually raises. For every
/// non-Active state the test additionally asserts the three "nothing happened" invariants — the message
/// store is empty, the update dispatcher recorded nothing, and no aggregate command was published — and
/// that the encrypted-file store was never touched (StoreUploadedCallCount == 0), proving the state check
/// runs strictly before file resolution even though valid file parts are pre-registered for the caller.
/// For the Active state the test asserts no error, and that send* stored exactly one message and
/// dispatched exactly one updateNewEncryptedMessage to the other party's bound device. Each run executes
/// a minimum of 100 generated cases.
/// </summary>
[Properties(Arbitrary = new[] { typeof(SecretChatStatePreconditionArbitraries) }, MaxTest = 100)]
public class Property04_StatePreconditionTests
{
    /// <summary>Client-chosen upload id used by the sendEncryptedFile cases.</summary>
    private const long ClientFileId = 777001;

    // 300 draws over a 5 x 3 x 2 combination space so every operation/state/caller triple is hit.
    [Property(Arbitrary = new[] { typeof(SecretChatStatePreconditionArbitraries) }, MaxTest = 300)]
    public void Operations_proceed_only_when_the_chat_is_active(SecretChatStatePreconditionCase @case)
    {
        var queryProcessor = new FakeQueryProcessor();
        var commandBus = new RecordingCommandBus();
        var dispatcher = new RecordingUpdateDispatcher();
        var messageStore = new InMemorySecretChatMessageStore();
        var fileStore = new InMemoryEncryptedFileStore();

        var chat = SecretChatTestHarness.Chat(@case.State);
        queryProcessor.Chats[chat.ChatId] = chat;
        queryProcessor.Users[SecretChatTestHarness.AdminId] = FakeUser.Create(SecretChatTestHarness.AdminId);
        queryProcessor.Users[SecretChatTestHarness.ParticipantId] =
            FakeUser.Create(SecretChatTestHarness.ParticipantId);

        // Valid file parts exist for both possible callers: a failure therefore can only come from the
        // state precondition, never from an unresolvable file.
        fileStore.Parts[(SecretChatTestHarness.AdminId, ClientFileId)] = [[1, 2, 3, 4]];
        fileStore.Parts[(SecretChatTestHarness.ParticipantId, ClientFileId)] = [[1, 2, 3, 4]];

        var service = new SecretChatAppService(commandBus,
            queryProcessor,
            new FakeIdGenerator(),
            new FakeBlockCacheAppService(),
            new SecretChatAccessResolver(queryProcessor),
            dispatcher,
            messageStore,
            new InMemorySecretChatRequestLedger(),
            fileStore,
            SecretChatTestHarness.ChatConverters(),
            SecretChatTestHarness.MessageConverters(),
            SecretChatTestHarness.FileConverters());

        var input = @case.CallerIsAdmin
            ? SecretChatTestHarness.Input(SecretChatTestHarness.AdminId, SecretChatTestHarness.AdminPermAuthKeyId)
            : SecretChatTestHarness.Input(SecretChatTestHarness.ParticipantId,
                SecretChatTestHarness.ParticipantPermAuthKeyId);

        var expectedError = ExpectedError(@case.Operation, @case.State);

        if (expectedError == null)
        {
            // ---- Active: the operation proceeds ------------------------------------------------
            Should.NotThrow(() => Invoke(service, input, @case));

            var expectedOtherUserId = @case.CallerIsAdmin
                ? SecretChatTestHarness.ParticipantId
                : SecretChatTestHarness.AdminId;
            var expectedOtherAuthKeyId = @case.CallerIsAdmin
                ? SecretChatTestHarness.ParticipantPermAuthKeyId
                : SecretChatTestHarness.AdminPermAuthKeyId;

            if (IsSend(@case.Operation))
            {
                messageStore.All.Count.ShouldBe(1);
                dispatcher.Dispatched.Count.ShouldBe(1);

                var dispatched = dispatcher.Dispatched[0];
                dispatched.Update.ShouldBeOfType<TUpdateNewEncryptedMessage>();
                dispatched.UserId.ShouldBe(expectedOtherUserId);
                dispatched.OnlySendToThisAuthKeyId.ShouldBe(expectedOtherAuthKeyId);
                dispatched.Qts.ShouldBe(SecretChatConsts.QtsInitialValue);
            }
            else
            {
                // read/typing never store a blob.
                messageStore.All.ShouldBeEmpty();

                // setEncryptedTyping(false) delivers nothing at all; read/typing(true) hit exactly the
                // other party's bound device.
                var expectedUpdates =
                    @case.Operation == SecretChatOperationKind.SetEncryptedTyping && !@case.Typing ? 0 : 1;
                dispatcher.Dispatched.Count.ShouldBe(expectedUpdates);

                if (expectedUpdates == 1)
                {
                    dispatcher.Dispatched[0].UserId.ShouldBe(expectedOtherUserId);
                    dispatcher.Dispatched[0].OnlySendToThisAuthKeyId.ShouldBe(expectedOtherAuthKeyId);
                }
            }

            // None of these five operations mutates the chat aggregate.
            commandBus.Published.ShouldBeEmpty();

            return;
        }

        // ---- Waiting / Discarded: the operation is rejected ------------------------------------
        var ex = Should.Throw<RpcException>(() => Invoke(service, input, @case));
        ex.RpcError.ShouldBe(expectedError.Value);

        // Nothing was stored, nothing changed state, nothing was delivered.
        messageStore.All.ShouldBeEmpty();
        dispatcher.Dispatched.ShouldBeEmpty();
        commandBus.Published.ShouldBeEmpty();

        // The state check strictly precedes file resolution, so the file store stays untouched.
        fileStore.StoreUploadedCallCount.ShouldBe(0);
        fileStore.ResolveCallCount.ShouldBe(0);

        // No qts is burned by a rejected operation, on either device.
        messageStore.GetHighestQtsAsync(SecretChatTestHarness.AdminId, SecretChatTestHarness.AdminPermAuthKeyId)
            .GetAwaiter().GetResult()
            .ShouldBe(SecretChatConsts.QtsInitialValue - 1);
        messageStore
            .GetHighestQtsAsync(SecretChatTestHarness.ParticipantId, SecretChatTestHarness.ParticipantPermAuthKeyId)
            .GetAwaiter().GetResult()
            .ShouldBe(SecretChatConsts.QtsInitialValue - 1);
    }

    /// <summary>
    /// The expected RPC error, derived independently of the production code from the operation/state pair:
    /// Active proceeds; Discarded is ENCRYPTION_DECLINED for send* and ENCRYPTION_ID_INVALID for
    /// read/typing; Waiting is always ENCRYPTION_ID_INVALID.
    /// </summary>
    private static RpcError? ExpectedError(SecretChatOperationKind operation, ChatState state)
    {
        if (state == ChatState.Active)
        {
            return null;
        }

        if (state == ChatState.Discarded && IsSend(operation))
        {
            return RpcErrors.RpcErrors400.EncryptionDeclined;
        }

        return RpcErrors.RpcErrors400.EncryptionIdInvalid;
    }

    private static bool IsSend(SecretChatOperationKind operation)
    {
        return operation is SecretChatOperationKind.SendEncrypted
            or SecretChatOperationKind.SendEncryptedFile
            or SecretChatOperationKind.SendEncryptedService;
    }

    private static void Invoke(SecretChatAppService service,
        TestRequestInput input,
        SecretChatStatePreconditionCase @case)
    {
        var peer = SecretChatTestHarness.InputChat();

        switch (@case.Operation)
        {
            case SecretChatOperationKind.SendEncrypted:
                service.SendEncryptedAsync(input, peer, @case.RandomId, @case.Data, silent: false)
                    .GetAwaiter().GetResult();
                break;
            case SecretChatOperationKind.SendEncryptedFile:
                service.SendEncryptedFileAsync(input,
                        peer,
                        @case.RandomId,
                        @case.Data,
                        new TInputEncryptedFileUploaded
                        {
                            Id = ClientFileId,
                            Parts = 1,
                            KeyFingerprint = 4242,
                            Md5Checksum = string.Empty
                        },
                        silent: false)
                    .GetAwaiter().GetResult();
                break;
            case SecretChatOperationKind.SendEncryptedService:
                service.SendEncryptedServiceAsync(input, peer, @case.RandomId, @case.Data)
                    .GetAwaiter().GetResult();
                break;
            case SecretChatOperationKind.ReadEncryptedHistory:
                service.ReadEncryptedHistoryAsync(input, peer, @case.MaxDate).GetAwaiter().GetResult();
                break;
            case SecretChatOperationKind.SetEncryptedTyping:
                service.SetEncryptedTypingAsync(input, peer, @case.Typing).GetAwaiter().GetResult();
                break;
            default:
                throw new NotSupportedException($"Unexpected operation {@case.Operation}");
        }
    }
}

/// <summary>The five secret-chat operations guarded by the "chat must be Active" precondition.</summary>
public enum SecretChatOperationKind
{
    SendEncrypted,
    SendEncryptedFile,
    SendEncryptedService,
    ReadEncryptedHistory,
    SetEncryptedTyping
}

/// <summary>
/// One generated state-precondition case: an operation, the stored chat state it is attempted against,
/// which side of the chat issues it, and the operation's payload parameters.
/// </summary>
public sealed record SecretChatStatePreconditionCase(SecretChatOperationKind Operation,
    ChatState State,
    bool CallerIsAdmin,
    long RandomId,
    byte[] Data,
    bool Typing,
    int MaxDate);

/// <summary>
/// FsCheck generators for Property 4. Only the case record above gets a custom generator — every field is
/// drawn from an explicit <c>Gen</c> so no primitive arbitrary is re-registered onto itself.
/// </summary>
public static class SecretChatStatePreconditionArbitraries
{
    public static Arbitrary<SecretChatStatePreconditionCase> Case() => Arb.From(CaseGen);

    private static Gen<SecretChatOperationKind> Operation =>
        Gen.Elements(SecretChatOperationKind.SendEncrypted,
            SecretChatOperationKind.SendEncryptedFile,
            SecretChatOperationKind.SendEncryptedService,
            SecretChatOperationKind.ReadEncryptedHistory,
            SecretChatOperationKind.SetEncryptedTyping);

    /// <summary>Only Waiting/Active/Discarded are ever persisted; Requested is a converter-only view.</summary>
    private static Gen<ChatState> StoredState =>
        Gen.Elements(ChatState.Waiting, ChatState.Active, ChatState.Discarded);

    private static Gen<byte[]> Payload =>
        from length in Gen.Choose(1, 64)
        from seed in Gen.Choose(0, 255)
        select BuildPayload(length, seed);

    private static Gen<SecretChatStatePreconditionCase> CaseGen =>
        from operation in Operation
        from state in StoredState
        from callerIsAdmin in Gen.Elements(true, false)
        from randomId in Gen.Choose(1, 1_000_000).Select(i => (long)i)
        from data in Payload
        from typing in Gen.Elements(true, false)
        from maxDate in Gen.Choose(0, 2_000_000_000)
        select new SecretChatStatePreconditionCase(operation, state, callerIsAdmin, randomId, data, typing,
            maxDate);

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
