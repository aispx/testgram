using FsCheck;
using FsCheck.Xunit;
using MyTelegram;
using MyTelegram.Messenger.Services.SecretChat;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.SecretChat;

#region Property 6 — update dispatch fan-out and addressing

/// <summary>
/// Feature: secret-chats, Property 6: Update dispatch fan-out and addressing.
///
/// For any operation producing an update, the update is delivered to exactly the target set of
/// Authorization_Keys with the correct exclusions, and every delivered update that carries <c>qts</c> gets
/// the <c>qts</c> assigned by the Qts_Sequencer for the corresponding device.
///
/// Validates: Requirements 3.3, 4.3, 4.4, 5.2, 5.7, 6.2, 7.2, 8.2, 9.2, 10.2.
///
/// How this is tested: <see cref="SecretChatFanOutCase"/> generates the operation
/// (requestEncryption / acceptEncryption / discardEncryption / sendEncrypted / sendEncryptedFile /
/// sendEncryptedService / readEncryptedHistory / setEncryptedTyping) together with a freshly generated
/// identity quadruple — admin user id, participant user id and their two distinct permanent
/// Authorization_Key ids — plus the chat id, the access_hash and the operation's nuisance parameters
/// (which side of the chat calls, the delete_history flag, the silent flag, random_id, max_date, payload
/// bytes). Nothing is hard-coded to the harness constants, so an addressing bug that happens to hit the
/// right constant cannot survive.
///
/// Each case drives the REAL <see cref="SecretChatAppService"/> wired to the REAL
/// <see cref="SecretChatAccessResolver"/>; only the transport (<see cref="RecordingUpdateDispatcher"/>),
/// the command bus and the stores are substituted. The recorded <see cref="DispatchedUpdate"/> tuples are
/// projected to <see cref="SecretChatDispatchShape"/> — (recipient user id, TL update kind including the
/// concrete <c>EncryptedChat</c> constructor inside <c>updateEncryption</c>, onlySendToThisAuthKeyId,
/// excludeAuthKeyId, qts) — and compared, in order and as a whole list, against the fan-out matrix
/// recomputed independently by <see cref="ExpectedFanOut"/>:
///
/// <list type="bullet">
/// <item>requestEncryption -> <c>updateEncryption(encryptedChatRequested)</c> to ALL of the target's
/// devices, no exclusion, no qts;</item>
/// <item>acceptEncryption -> <c>updateEncryption(encryptedChat)</c> to the admin's BOUND device, then
/// <c>updateEncryption(encryptedChatDiscarded)</c> to the accepting party's OTHER devices (the accepting
/// Authorization_Key excluded);</item>
/// <item>discardEncryption -> <c>updateEncryption(encryptedChatDiscarded)</c> to ALL devices of the other
/// party, then to the caller's OTHER devices (the issuing Authorization_Key excluded);</item>
/// <item>sendEncrypted / sendEncryptedFile / sendEncryptedService -> <c>updateNewEncryptedMessage</c> to
/// the other party's BOUND device only, carrying qts;</item>
/// <item>readEncryptedHistory -> <c>updateEncryptedMessagesRead</c> to the other party's bound device
/// only;</item>
/// <item>setEncryptedTyping(true) -> <c>updateEncryptedChatTyping</c> to the other party's bound device
/// only; setEncryptedTyping(false) -> nothing.</item>
/// </list>
///
/// The qts half of the property is asserted independently of the operation, as a dispatcher-level
/// invariant: a dispatch carries a qts if and only if its update is an <c>updateNewEncryptedMessage</c>,
/// the qts on the transport equals the qts inside the TL update, and both equal the value the sequencer
/// allocated for the RECIPIENT device (the store's highest allocated qts for exactly that
/// (userId, permAuthKeyId) pair, which for a fresh device is
/// <see cref="SecretChatConsts.QtsInitialValue"/>), while the sender's own device gets no allocation.
/// Explicit per-method tests then pin down the payloads (g_a / g_b / key_fingerprint / history_deleted /
/// bytes / max_date) and the push-notification descriptors that the shape comparison abstracts away.
/// Each property runs a minimum of 100 generated cases.
/// </summary>
public class Property06_FanOutAddressingTests
{
    // 400 draws over an 8-operation x 2-caller-side x 2-flag space so every operation/side pair is hit.
    [Property(Arbitrary = new[] { typeof(SecretChatFanOutArbitraries) }, MaxTest = 400)]
    public void Every_operation_delivers_exactly_its_documented_fan_out(SecretChatFanOutCase @case)
    {
        var world = new SecretChatFanOutWorld(@case);

        world.Invoke();

        var actual = world.Dispatcher.Dispatched.Select(SecretChatDispatchShape.Of).ToList();

        actual.ShouldBe(ExpectedFanOut(@case));
    }

    /// <summary>
    /// The qts half of the property, stated as an addressing invariant that does not depend on which
    /// operation ran: only <c>updateNewEncryptedMessage</c> carries a qts, that qts is the sequencer value
    /// of the RECIPIENT device, and it is identical on the transport and inside the TL update.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(SecretChatFanOutArbitraries) }, MaxTest = 400)]
    public void Only_updateNewEncryptedMessage_carries_the_recipient_devices_sequencer_qts(
        SecretChatFanOutCase @case)
    {
        var world = new SecretChatFanOutWorld(@case);

        world.Invoke();

        foreach (var dispatch in world.Dispatcher.Dispatched)
        {
            // A device-targeted push names exactly one key and excludes none; a broadcast names none.
            if (dispatch.OnlySendToThisAuthKeyId.HasValue)
            {
                dispatch.ExcludeAuthKeyId.ShouldBeNull();
            }

            dispatch.Qts.HasValue.ShouldBe(dispatch.Update is TUpdateNewEncryptedMessage);

            if (dispatch.Update is not TUpdateNewEncryptedMessage newEncryptedMessage)
            {
                continue;
            }

            // A qts-bearing update is always addressed to a single bound device.
            dispatch.OnlySendToThisAuthKeyId.ShouldNotBeNull();

            var sequencerQts = world.MessageStore
                .GetHighestQtsAsync(dispatch.UserId, dispatch.OnlySendToThisAuthKeyId!.Value)
                .GetAwaiter().GetResult();

            dispatch.Qts!.Value.ShouldBe(sequencerQts);
            newEncryptedMessage.Qts.ShouldBe(sequencerQts);
            // Fresh recipient device: the first allocated value is the fixed initial qts.
            sequencerQts.ShouldBe(SecretChatConsts.QtsInitialValue);

            // The sender's own device is not part of the recipient's temporary update box.
            world.MessageStore.GetHighestQtsAsync(world.CallerUserId, world.CallerPermAuthKeyId)
                .GetAwaiter().GetResult()
                .ShouldBe(SecretChatConsts.QtsInitialValue - 1);
        }
    }

    // ---- Per-method addressing and payload assertions ----------------------------------------

    [Fact]
    public void RequestEncryption_pushes_encryptedChatRequested_to_all_devices_of_the_target()
    {
        var world = new SecretChatFanOutWorld(FixedCase(SecretChatFanOutOperation.RequestEncryption));

        var waiting = (TEncryptedChatWaiting)world.Invoke()!;

        var dispatch = world.Dispatcher.Dispatched.ShouldHaveSingleItem();
        dispatch.UserId.ShouldBe(world.Ids.ParticipantId);
        // PushToAllDevicesAsync: no bound device yet, and nothing is excluded.
        dispatch.OnlySendToThisAuthKeyId.ShouldBeNull();
        dispatch.ExcludeAuthKeyId.ShouldBeNull();
        dispatch.Qts.ShouldBeNull();

        var update = dispatch.Update.ShouldBeOfType<TUpdateEncryption>();
        var requested = update.Chat.ShouldBeOfType<TEncryptedChatRequested>();

        // The requested chat delivered to B mirrors the waiting chat returned to A, g_a included.
        requested.Id.ShouldBe(waiting.Id);
        requested.AccessHash.ShouldBe(waiting.AccessHash);
        requested.Date.ShouldBe(waiting.Date);
        requested.AdminId.ShouldBe(world.Ids.AdminId);
        requested.ParticipantId.ShouldBe(world.Ids.ParticipantId);
        requested.GA.ShouldBe(world.Ga);

        dispatch.PushData.ShouldNotBeNull();
        dispatch.PushData!.LocKey.ShouldBe(PushNotificationTypes.EncryptionRequest);
        dispatch.PushData.UserId.ShouldBe(world.Ids.ParticipantId);
        dispatch.PushData.Custom!.EncryptionId.ShouldBe((long)waiting.Id);
    }

    [Fact]
    public void AcceptEncryption_pushes_encryptedChat_to_the_admins_bound_device_and_discarded_elsewhere()
    {
        var world = new SecretChatFanOutWorld(FixedCase(SecretChatFanOutOperation.AcceptEncryption));

        var accepted = (TEncryptedChat)world.Invoke()!;

        world.Dispatcher.Dispatched.Count.ShouldBe(2);

        // (1) The requester's BOUND device receives the established chat with the receiver's g_b.
        var toAdmin = world.Dispatcher.Dispatched[0];
        toAdmin.UserId.ShouldBe(world.Ids.AdminId);
        toAdmin.OnlySendToThisAuthKeyId.ShouldBe(world.Ids.AdminPermAuthKeyId);
        toAdmin.ExcludeAuthKeyId.ShouldBeNull();
        toAdmin.Qts.ShouldBeNull();

        var adminChat = toAdmin.Update.ShouldBeOfType<TUpdateEncryption>().Chat
            .ShouldBeOfType<TEncryptedChat>();
        adminChat.Id.ShouldBe(world.Ids.ChatId);
        adminChat.AccessHash.ShouldBe(world.Ids.AccessHash);
        adminChat.AdminId.ShouldBe(world.Ids.AdminId);
        adminChat.ParticipantId.ShouldBe(world.Ids.ParticipantId);
        adminChat.GAOrB.ShouldBe(world.Gb);
        adminChat.KeyFingerprint.ShouldBe(SecretChatFanOutWorld.KeyFingerprint);

        // The accepting device gets the admin's g_a back in the RPC result, not g_b.
        accepted.GAOrB.ShouldBe(world.Ga);

        // (2) The receiver's OTHER devices drop the pending request; the accepting key is excluded.
        var toOtherDevices = world.Dispatcher.Dispatched[1];
        toOtherDevices.UserId.ShouldBe(world.Ids.ParticipantId);
        toOtherDevices.OnlySendToThisAuthKeyId.ShouldBeNull();
        toOtherDevices.ExcludeAuthKeyId.ShouldBe(world.Ids.ParticipantPermAuthKeyId);
        toOtherDevices.Qts.ShouldBeNull();
        toOtherDevices.Update.ShouldBeOfType<TUpdateEncryption>().Chat
            .ShouldBeOfType<TEncryptedChatDiscarded>()
            .Id.ShouldBe(world.Ids.ChatId);

        // No dispatch is addressed to the accepting Authorization_Key itself.
        world.Dispatcher.Dispatched
            .ShouldNotContain(d => d.OnlySendToThisAuthKeyId == world.Ids.ParticipantPermAuthKeyId);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void DiscardEncryption_reaches_the_other_party_and_the_callers_other_devices(bool callerIsAdmin,
        bool deleteHistory)
    {
        var world = new SecretChatFanOutWorld(FixedCase(SecretChatFanOutOperation.DiscardEncryption,
            callerIsAdmin: callerIsAdmin,
            deleteHistory: deleteHistory));

        world.Invoke();

        world.Dispatcher.Dispatched.Count.ShouldBe(2);

        // (1) Every device of the other participant — no exclusion.
        var toOther = world.Dispatcher.Dispatched[0];
        toOther.UserId.ShouldBe(world.OtherUserId);
        toOther.OnlySendToThisAuthKeyId.ShouldBeNull();
        toOther.ExcludeAuthKeyId.ShouldBeNull();

        // (2) The caller's own devices — except the one that issued the discard.
        var toSelf = world.Dispatcher.Dispatched[1];
        toSelf.UserId.ShouldBe(world.CallerUserId);
        toSelf.OnlySendToThisAuthKeyId.ShouldBeNull();
        toSelf.ExcludeAuthKeyId.ShouldBe(world.CallerPermAuthKeyId);

        foreach (var dispatch in world.Dispatcher.Dispatched)
        {
            dispatch.Qts.ShouldBeNull();

            var discarded = dispatch.Update.ShouldBeOfType<TUpdateEncryption>().Chat
                .ShouldBeOfType<TEncryptedChatDiscarded>();
            discarded.Id.ShouldBe(world.Ids.ChatId);
            discarded.HistoryDeleted.ShouldBe(deleteHistory);
        }
    }

    [Theory]
    [InlineData(SecretChatFanOutOperation.SendEncrypted, true)]
    [InlineData(SecretChatFanOutOperation.SendEncrypted, false)]
    [InlineData(SecretChatFanOutOperation.SendEncryptedFile, true)]
    [InlineData(SecretChatFanOutOperation.SendEncryptedService, true)]
    [InlineData(SecretChatFanOutOperation.SendEncryptedService, false)]
    public void Send_operations_address_only_the_other_partys_bound_device_and_carry_the_sequencer_qts(
        SecretChatFanOutOperation operation,
        bool callerIsAdmin)
    {
        var world = new SecretChatFanOutWorld(FixedCase(operation, callerIsAdmin: callerIsAdmin));

        world.Invoke();

        var dispatch = world.Dispatcher.Dispatched.ShouldHaveSingleItem();
        dispatch.UserId.ShouldBe(world.OtherUserId);
        dispatch.OnlySendToThisAuthKeyId.ShouldBe(world.OtherPermAuthKeyId);
        dispatch.ExcludeAuthKeyId.ShouldBeNull();

        var update = dispatch.Update.ShouldBeOfType<TUpdateNewEncryptedMessage>();

        // The qts the sequencer allocated for the recipient device, on the transport, in the TL update
        // and on the stored row alike.
        var allocated = world.MessageStore
            .GetHighestQtsAsync(world.OtherUserId, world.OtherPermAuthKeyId).GetAwaiter().GetResult();
        allocated.ShouldBe(SecretChatConsts.QtsInitialValue);
        dispatch.Qts.ShouldBe(allocated);
        update.Qts.ShouldBe(allocated);
        world.MessageStore.All.ShouldHaveSingleItem().Qts.ShouldBe(allocated);

        // The payload is relayed byte-for-byte, in the constructor the operation calls for.
        if (operation == SecretChatFanOutOperation.SendEncryptedService)
        {
            var service = update.Message.ShouldBeOfType<TEncryptedMessageService>();
            service.ChatId.ShouldBe(world.Ids.ChatId);
            service.RandomId.ShouldBe(world.Case.RandomId);
            service.Bytes.ShouldBe(world.Case.Data);
        }
        else
        {
            var message = update.Message.ShouldBeOfType<TEncryptedMessage>();
            message.ChatId.ShouldBe(world.Ids.ChatId);
            message.RandomId.ShouldBe(world.Case.RandomId);
            message.Bytes.ShouldBe(world.Case.Data);
        }
    }

    /// <summary>
    /// Three sends on the same chat consume the recipient device's sequence 1, 2, 3 — each dispatch carries
    /// the value allocated for it, and the sender's own device never advances.
    /// </summary>
    [Fact]
    public void Consecutive_sends_carry_consecutive_recipient_device_qts_values()
    {
        var world = new SecretChatFanOutWorld(FixedCase(SecretChatFanOutOperation.SendEncrypted));

        for (var i = 0; i < 3; i++)
        {
            world.Service
                .SendEncryptedAsync(world.Input, world.Peer, world.Case.RandomId + i, world.Case.Data,
                    silent: false)
                .GetAwaiter().GetResult();
        }

        world.Dispatcher.Dispatched.Count.ShouldBe(3);
        world.Dispatcher.Dispatched.Select(d => d.Qts)
            .ShouldBe([
                SecretChatConsts.QtsInitialValue,
                SecretChatConsts.QtsInitialValue + 1,
                SecretChatConsts.QtsInitialValue + 2
            ]);
        world.Dispatcher.Dispatched
            .Select(d => ((TUpdateNewEncryptedMessage)d.Update).Qts)
            .ShouldBe([
                SecretChatConsts.QtsInitialValue,
                SecretChatConsts.QtsInitialValue + 1,
                SecretChatConsts.QtsInitialValue + 2
            ]);

        world.MessageStore.GetHighestQtsAsync(world.CallerUserId, world.CallerPermAuthKeyId)
            .GetAwaiter().GetResult()
            .ShouldBe(SecretChatConsts.QtsInitialValue - 1);
    }

    /// <summary>
    /// A duplicate (chat, sender, random_id) is a no-op for the fan-out: no second dispatch, and no qts is
    /// burned on the recipient device.
    /// </summary>
    [Fact]
    public void A_duplicate_send_delivers_no_second_update_and_burns_no_qts()
    {
        var world = new SecretChatFanOutWorld(FixedCase(SecretChatFanOutOperation.SendEncrypted));

        world.Invoke();
        world.Invoke();

        world.Dispatcher.Dispatched.ShouldHaveSingleItem();
        world.MessageStore.GetHighestQtsAsync(world.OtherUserId, world.OtherPermAuthKeyId)
            .GetAwaiter().GetResult()
            .ShouldBe(SecretChatConsts.QtsInitialValue);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadEncryptedHistory_addresses_only_the_other_partys_bound_device(bool callerIsAdmin)
    {
        var world = new SecretChatFanOutWorld(FixedCase(SecretChatFanOutOperation.ReadEncryptedHistory,
            callerIsAdmin: callerIsAdmin));

        world.Invoke();

        var dispatch = world.Dispatcher.Dispatched.ShouldHaveSingleItem();
        dispatch.UserId.ShouldBe(world.OtherUserId);
        dispatch.OnlySendToThisAuthKeyId.ShouldBe(world.OtherPermAuthKeyId);
        dispatch.ExcludeAuthKeyId.ShouldBeNull();
        dispatch.Qts.ShouldBeNull();

        var read = dispatch.Update.ShouldBeOfType<TUpdateEncryptedMessagesRead>();
        read.ChatId.ShouldBe(world.Ids.ChatId);
        read.MaxDate.ShouldBe(world.Case.MaxDate);

        // Never echoed back to the reader.
        world.Dispatcher.Dispatched.ShouldNotContain(d => d.UserId == world.CallerUserId);
    }

    // ---- Expected fan-out, recomputed independently of the production code --------------------

    /// <summary>
    /// The authoritative fan-out matrix: recipient user, TL update kind, onlySendToThisAuthKeyId,
    /// excludeAuthKeyId and qts for every dispatch each operation is allowed to make, in order.
    /// </summary>
    private static IReadOnlyList<SecretChatDispatchShape> ExpectedFanOut(SecretChatFanOutCase @case)
    {
        var ids = @case.Ids;
        var callerIsAdmin = SecretChatFanOutWorld.EffectiveCallerIsAdmin(@case);
        var callerUserId = callerIsAdmin ? ids.AdminId : ids.ParticipantId;
        var callerKey = callerIsAdmin ? ids.AdminPermAuthKeyId : ids.ParticipantPermAuthKeyId;
        var otherUserId = callerIsAdmin ? ids.ParticipantId : ids.AdminId;
        var otherKey = callerIsAdmin ? ids.ParticipantPermAuthKeyId : ids.AdminPermAuthKeyId;

        switch (@case.Operation)
        {
            case SecretChatFanOutOperation.RequestEncryption:
                // Requirement 3.3: every Authorization_Key of the target, none excluded.
                return
                [
                    new SecretChatDispatchShape(ids.ParticipantId, SecretChatUpdateKind.EncryptionRequested,
                        null, null, null)
                ];

            case SecretChatFanOutOperation.AcceptEncryption:
                // Requirements 4.3 / 4.4: the admin's bound device, then the receiver's OTHER devices.
                return
                [
                    new SecretChatDispatchShape(ids.AdminId, SecretChatUpdateKind.EncryptionEstablished,
                        ids.AdminPermAuthKeyId, null, null),
                    new SecretChatDispatchShape(ids.ParticipantId, SecretChatUpdateKind.EncryptionDiscarded,
                        null, ids.ParticipantPermAuthKeyId, null)
                ];

            case SecretChatFanOutOperation.DiscardEncryption:
                // Requirements 5.2 / 5.7: all devices of the other party, then the caller's other devices.
                return
                [
                    new SecretChatDispatchShape(otherUserId, SecretChatUpdateKind.EncryptionDiscarded, null,
                        null, null),
                    new SecretChatDispatchShape(callerUserId, SecretChatUpdateKind.EncryptionDiscarded, null,
                        callerKey, null)
                ];

            case SecretChatFanOutOperation.SendEncrypted:
            case SecretChatFanOutOperation.SendEncryptedFile:
            case SecretChatFanOutOperation.SendEncryptedService:
                // Requirements 6.2 / 7.2 / 8.2: the other party's bound device, carrying the first qts.
                return
                [
                    new SecretChatDispatchShape(otherUserId, SecretChatUpdateKind.NewEncryptedMessage,
                        otherKey, null, SecretChatConsts.QtsInitialValue)
                ];

            case SecretChatFanOutOperation.ReadEncryptedHistory:
                // Requirement 9.2.
                return
                [
                    new SecretChatDispatchShape(otherUserId, SecretChatUpdateKind.MessagesRead, otherKey,
                        null, null)
                ];

            case SecretChatFanOutOperation.SetEncryptedTyping:
                // Requirement 10.2: typing=true reaches the other party only; typing=false reaches nobody.
                return @case.Typing
                    ?
                    [
                        new SecretChatDispatchShape(otherUserId, SecretChatUpdateKind.ChatTyping, otherKey,
                            null, null)
                    ]
                    : [];

            default:
                throw new NotSupportedException($"Unexpected operation {@case.Operation}");
        }
    }

    private static SecretChatFanOutCase FixedCase(SecretChatFanOutOperation operation,
        bool callerIsAdmin = true,
        bool typing = true,
        bool deleteHistory = false,
        bool discardFromWaiting = false)
    {
        return new SecretChatFanOutCase(operation,
            new SecretChatIdentityFixture(AdminId: 1001,
                ParticipantId: 2002,
                AdminPermAuthKeyId: 111,
                ParticipantPermAuthKeyId: 222,
                ChatId: 5,
                AccessHash: 987654321),
            callerIsAdmin,
            typing,
            deleteHistory,
            discardFromWaiting,
            Silent: false,
            RandomId: 990001,
            RequestRandomId: 4242,
            MaxDate: 1_700_000_000,
            Data: SecretChatTestHarness.Payload(9, 8, 7, 6, 5));
    }
}

#endregion

#region Property 7 — setEncryptedTyping is never addressed to the caller

/// <summary>
/// Feature: secret-chats, Property 7: setEncryptedTyping is never addressed to the caller.
///
/// For any established chat, with <c>typing=true</c> an <c>updateEncryptedChatTyping</c> is delivered to the
/// other participant's device and to NO device of the caller; with <c>typing=false</c> <c>boolTrue</c> is
/// returned and no <c>updateEncryptedChatTyping</c> is delivered at all.
///
/// Validates: Requirements 10.1, 10.2, 10.3.
///
/// How this is tested: <see cref="SecretChatTypingCase"/> generates the typing flag, which side of the chat
/// issues the request, and a fresh identity quadruple (both user ids and both permanent Authorization_Key
/// ids, all distinct and unrelated to the harness constants). Each case drives the REAL
/// <see cref="SecretChatAppService"/> over the REAL <see cref="SecretChatAccessResolver"/> and asserts,
/// independently of the fan-out matrix used by Property 6, that:
/// (a) the RPC result is <c>boolTrue</c> in both directions of the flag;
/// (b) with typing=true exactly one update is dispatched, it is an <c>updateEncryptedChatTyping</c> carrying
/// the chat id, it is addressed to the other participant's BOUND Authorization_Key (a device-targeted push,
/// not a broadcast), and it carries no qts;
/// (c) no dispatch at all — regardless of update type — names the caller's user id or the caller's
/// Authorization_Key, neither as a recipient nor via onlySendToThisAuthKeyId/excludeAuthKeyId, so a typing
/// notification can never be echoed back to any of the caller's devices;
/// (d) with typing=false the dispatcher records nothing at all, and in particular no
/// <c>updateEncryptedChatTyping</c>;
/// (e) neither branch stores a blob, publishes an aggregate command or burns a qts on either device.
/// Each property runs a minimum of 100 generated cases.
/// </summary>
public class Property07_TypingAddressingTests
{
    [Property(Arbitrary = new[] { typeof(SecretChatTypingArbitraries) }, MaxTest = 200)]
    public void Typing_reaches_the_other_participant_only_and_never_the_caller(SecretChatTypingCase @case)
    {
        var world = new SecretChatFanOutWorld(@case.ToFanOutCase());

        var result = world.Invoke();

        // (a) boolTrue either way.
        result.ShouldBeOfType<TBoolTrue>();

        if (@case.Typing)
        {
            // (b) Exactly one update, addressed to the other party's bound device.
            var dispatch = world.Dispatcher.Dispatched.ShouldHaveSingleItem();
            dispatch.Update.ShouldBeOfType<TUpdateEncryptedChatTyping>().ChatId.ShouldBe(world.Ids.ChatId);
            dispatch.UserId.ShouldBe(world.OtherUserId);
            dispatch.OnlySendToThisAuthKeyId.ShouldBe(world.OtherPermAuthKeyId);
            dispatch.ExcludeAuthKeyId.ShouldBeNull();
            dispatch.Qts.ShouldBeNull();
        }
        else
        {
            // (d) typing=false delivers nothing whatsoever.
            world.Dispatcher.Dispatched.ShouldBeEmpty();
        }

        // (c) Nothing is ever addressed to the caller — not the user, not the Authorization_Key.
        world.Dispatcher.Dispatched.ShouldNotContain(d => d.UserId == world.CallerUserId);
        world.Dispatcher.Dispatched
            .ShouldNotContain(d => d.OnlySendToThisAuthKeyId == world.CallerPermAuthKeyId);
        world.Dispatcher.Dispatched.ShouldNotContain(d => d.ExcludeAuthKeyId == world.CallerPermAuthKeyId);
        world.Dispatcher.Dispatched.Count(d => d.Update is TUpdateEncryptedChatTyping)
            .ShouldBe(@case.Typing ? 1 : 0);

        // (e) Typing is a pure relay: no blob, no aggregate command, no qts consumed on either device.
        world.MessageStore.All.ShouldBeEmpty();
        world.CommandBus.Published.ShouldBeEmpty();
        world.MessageStore.GetHighestQtsAsync(world.Ids.AdminId, world.Ids.AdminPermAuthKeyId)
            .GetAwaiter().GetResult()
            .ShouldBe(SecretChatConsts.QtsInitialValue - 1);
        world.MessageStore.GetHighestQtsAsync(world.Ids.ParticipantId, world.Ids.ParticipantPermAuthKeyId)
            .GetAwaiter().GetResult()
            .ShouldBe(SecretChatConsts.QtsInitialValue - 1);
    }

    /// <summary>
    /// The same statement pinned down as a table, from both sides of the chat: the admin typing reaches the
    /// participant's key only, the participant typing reaches the admin's key only, and typing=false
    /// reaches nobody.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Typing_addressing_matrix(bool callerIsAdmin, bool typing)
    {
        var ids = new SecretChatIdentityFixture(AdminId: 4001,
            ParticipantId: 5002,
            AdminPermAuthKeyId: 6003,
            ParticipantPermAuthKeyId: 7004,
            ChatId: 11,
            AccessHash: 555000111);
        var world = new SecretChatFanOutWorld(new SecretChatTypingCase(ids, callerIsAdmin, typing)
            .ToFanOutCase());

        world.Invoke().ShouldBeOfType<TBoolTrue>();

        if (!typing)
        {
            world.Dispatcher.Dispatched.ShouldBeEmpty();

            return;
        }

        var dispatch = world.Dispatcher.Dispatched.ShouldHaveSingleItem();
        dispatch.UserId.ShouldBe(callerIsAdmin ? ids.ParticipantId : ids.AdminId);
        dispatch.OnlySendToThisAuthKeyId
            .ShouldBe(callerIsAdmin ? ids.ParticipantPermAuthKeyId : ids.AdminPermAuthKeyId);
        dispatch.Update.ShouldBeOfType<TUpdateEncryptedChatTyping>().ChatId.ShouldBe(ids.ChatId);
    }
}

#endregion

// ---- Generated cases, shapes and generators --------------------------------------------------

/// <summary>The secret-chat operations that produce a fan-out, one per row of the dispatch matrix.</summary>
public enum SecretChatFanOutOperation
{
    RequestEncryption,
    AcceptEncryption,
    DiscardEncryption,
    SendEncrypted,
    SendEncryptedFile,
    SendEncryptedService,
    ReadEncryptedHistory,
    SetEncryptedTyping
}

/// <summary>
/// The TL identity of a dispatched update, including the concrete <c>EncryptedChat</c> constructor carried
/// inside an <c>updateEncryption</c> (the three <c>updateEncryption</c> rows of the matrix differ only in
/// that inner constructor).
/// </summary>
public enum SecretChatUpdateKind
{
    EncryptionWaiting,
    EncryptionRequested,
    EncryptionEstablished,
    EncryptionDiscarded,
    NewEncryptedMessage,
    MessagesRead,
    ChatTyping,
    Other
}

/// <summary>The four identities a secret chat is addressed by, plus the chat id and its access_hash.</summary>
public sealed record SecretChatIdentityFixture(
    long AdminId,
    long ParticipantId,
    long AdminPermAuthKeyId,
    long ParticipantPermAuthKeyId,
    int ChatId,
    long AccessHash);

/// <summary>
/// One generated fan-out case: the operation, the identities it runs against, which side of the chat calls
/// it, and the operation's nuisance parameters.
/// </summary>
public sealed record SecretChatFanOutCase(
    SecretChatFanOutOperation Operation,
    SecretChatIdentityFixture Ids,
    bool CallerIsAdmin,
    bool Typing,
    bool DeleteHistory,
    bool DiscardFromWaiting,
    bool Silent,
    long RandomId,
    int RequestRandomId,
    int MaxDate,
    byte[] Data)
{
    public override string ToString()
    {
        return $"FanOutCase(op={Operation}, admin={Ids.AdminId}/{Ids.AdminPermAuthKeyId}, " +
               $"participant={Ids.ParticipantId}/{Ids.ParticipantPermAuthKeyId}, chat={Ids.ChatId}, " +
               $"callerIsAdmin={CallerIsAdmin}, typing={Typing}, deleteHistory={DeleteHistory}, " +
               $"discardFromWaiting={DiscardFromWaiting}, silent={Silent})";
    }
}

/// <summary>A generated setEncryptedTyping case for Property 7.</summary>
public sealed record SecretChatTypingCase(SecretChatIdentityFixture Ids, bool CallerIsAdmin, bool Typing)
{
    public SecretChatFanOutCase ToFanOutCase()
    {
        return new SecretChatFanOutCase(SecretChatFanOutOperation.SetEncryptedTyping,
            Ids,
            CallerIsAdmin,
            Typing,
            DeleteHistory: false,
            DiscardFromWaiting: false,
            Silent: false,
            RandomId: 1,
            RequestRandomId: 1,
            MaxDate: 0,
            Data: SecretChatTestHarness.Payload(1));
    }

    public override string ToString()
    {
        return $"TypingCase(admin={Ids.AdminId}/{Ids.AdminPermAuthKeyId}, " +
               $"participant={Ids.ParticipantId}/{Ids.ParticipantPermAuthKeyId}, " +
               $"callerIsAdmin={CallerIsAdmin}, typing={Typing})";
    }
}

/// <summary>
/// The addressing-relevant projection of a recorded dispatch: who receives it, which TL update it is, the
/// single bound device it is pinned to (<c>PushToDeviceAsync</c>), the device it excludes
/// (<c>PushToAllDevicesAsync</c>) and the qts it carries.
/// </summary>
public sealed record SecretChatDispatchShape(
    long UserId,
    SecretChatUpdateKind Kind,
    long? OnlySendToThisAuthKeyId,
    long? ExcludeAuthKeyId,
    int? Qts)
{
    internal static SecretChatDispatchShape Of(DispatchedUpdate dispatched)
    {
        return new SecretChatDispatchShape(dispatched.UserId,
            KindOf(dispatched.Update),
            dispatched.OnlySendToThisAuthKeyId,
            dispatched.ExcludeAuthKeyId,
            dispatched.Qts);
    }

    private static SecretChatUpdateKind KindOf(IUpdate update)
    {
        return update switch
        {
            TUpdateEncryption encryption => encryption.Chat switch
            {
                TEncryptedChatWaiting => SecretChatUpdateKind.EncryptionWaiting,
                TEncryptedChatRequested => SecretChatUpdateKind.EncryptionRequested,
                TEncryptedChat => SecretChatUpdateKind.EncryptionEstablished,
                TEncryptedChatDiscarded => SecretChatUpdateKind.EncryptionDiscarded,
                _ => SecretChatUpdateKind.Other
            },
            TUpdateNewEncryptedMessage => SecretChatUpdateKind.NewEncryptedMessage,
            TUpdateEncryptedMessagesRead => SecretChatUpdateKind.MessagesRead,
            TUpdateEncryptedChatTyping => SecretChatUpdateKind.ChatTyping,
            _ => SecretChatUpdateKind.Other
        };
    }
}

/// <summary>
/// One fully wired secret-chat world for a generated fan-out case: the REAL
/// <see cref="SecretChatAppService"/> over the REAL <see cref="SecretChatAccessResolver"/>, with the
/// harness fakes standing in for the transport, the command bus and the stores.
/// </summary>
internal sealed class SecretChatFanOutWorld
{
    /// <summary>Client-chosen upload id used by the sendEncryptedFile case.</summary>
    private const long ClientFileId = 770099;

    public const long KeyFingerprint = 987_654_321_012L;

    public SecretChatFanOutWorld(SecretChatFanOutCase @case)
    {
        Case = @case;
        Ids = @case.Ids;
        CallerIsAdmin = EffectiveCallerIsAdmin(@case);
        CallerUserId = CallerIsAdmin ? Ids.AdminId : Ids.ParticipantId;
        CallerPermAuthKeyId = CallerIsAdmin ? Ids.AdminPermAuthKeyId : Ids.ParticipantPermAuthKeyId;
        OtherUserId = CallerIsAdmin ? Ids.ParticipantId : Ids.AdminId;
        OtherPermAuthKeyId = CallerIsAdmin ? Ids.ParticipantPermAuthKeyId : Ids.AdminPermAuthKeyId;

        QueryProcessor.Users[Ids.AdminId] = FakeUser.Create(Ids.AdminId);
        QueryProcessor.Users[Ids.ParticipantId] = FakeUser.Create(Ids.ParticipantId);

        if (@case.Operation != SecretChatFanOutOperation.RequestEncryption)
        {
            // requestEncryption creates the chat; every other operation resolves an existing one.
            var chat = BuildChat(Ids, StateFor(@case));
            QueryProcessor.Chats[chat.ChatId] = chat;
        }

        // Valid parts for the sendEncryptedFile case, so the file always resolves.
        FileStore.Parts[(CallerUserId, ClientFileId)] = [[3, 1, 4, 1, 5]];

        Input = SecretChatTestHarness.Input(CallerUserId, CallerPermAuthKeyId);
        Peer = SecretChatTestHarness.InputChat(Ids.AccessHash, Ids.ChatId);

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

    public SecretChatFanOutCase Case { get; }
    public SecretChatIdentityFixture Ids { get; }
    public bool CallerIsAdmin { get; }
    public long CallerUserId { get; }
    public long CallerPermAuthKeyId { get; }
    public long OtherUserId { get; }
    public long OtherPermAuthKeyId { get; }

    /// <summary>The requester's g_a; also the g_a stored on an existing chat.</summary>
    public byte[] Ga { get; } = DhValue(0xA1);

    /// <summary>The receiver's g_b, deliberately different from <see cref="Ga"/>.</summary>
    public byte[] Gb { get; } = DhValue(0xB2);

    public FakeQueryProcessor QueryProcessor { get; } = new();
    public RecordingCommandBus CommandBus { get; } = new();
    public RecordingUpdateDispatcher Dispatcher { get; } = new();
    public InMemorySecretChatMessageStore MessageStore { get; } = new();
    public InMemoryEncryptedFileStore FileStore { get; } = new();
    public SecretChatAppService Service { get; }
    public TestRequestInput Input { get; }
    public IInputEncryptedChat Peer { get; }

    /// <summary>
    /// Which side of the chat actually issues the request. requestEncryption is by definition issued by the
    /// admin (it creates the chat), and only the receiver may accept; for every other operation the
    /// generated toggle decides.
    /// </summary>
    public static bool EffectiveCallerIsAdmin(SecretChatFanOutCase @case)
    {
        return @case.Operation switch
        {
            SecretChatFanOutOperation.RequestEncryption => true,
            SecretChatFanOutOperation.AcceptEncryption => false,
            _ => @case.CallerIsAdmin
        };
    }

    public object? Invoke()
    {
        switch (Case.Operation)
        {
            case SecretChatFanOutOperation.RequestEncryption:
                return Service
                    .RequestEncryptionAsync(Input,
                        new TInputUser { UserId = Ids.ParticipantId, AccessHash = 0 },
                        Case.RequestRandomId,
                        Ga)
                    .GetAwaiter().GetResult();

            case SecretChatFanOutOperation.AcceptEncryption:
                return Service.AcceptEncryptionAsync(Input, Peer, Gb, KeyFingerprint).GetAwaiter().GetResult();

            case SecretChatFanOutOperation.DiscardEncryption:
                return Service.DiscardEncryptionAsync(Input, Ids.ChatId, Case.DeleteHistory)
                    .GetAwaiter().GetResult();

            case SecretChatFanOutOperation.SendEncrypted:
                return Service.SendEncryptedAsync(Input, Peer, Case.RandomId, Case.Data, Case.Silent)
                    .GetAwaiter().GetResult();

            case SecretChatFanOutOperation.SendEncryptedFile:
                return Service.SendEncryptedFileAsync(Input,
                        Peer,
                        Case.RandomId,
                        Case.Data,
                        new TInputEncryptedFileUploaded
                        {
                            Id = ClientFileId,
                            Parts = 1,
                            KeyFingerprint = 4242,
                            Md5Checksum = string.Empty
                        },
                        Case.Silent)
                    .GetAwaiter().GetResult();

            case SecretChatFanOutOperation.SendEncryptedService:
                return Service.SendEncryptedServiceAsync(Input, Peer, Case.RandomId, Case.Data)
                    .GetAwaiter().GetResult();

            case SecretChatFanOutOperation.ReadEncryptedHistory:
                return Service.ReadEncryptedHistoryAsync(Input, Peer, Case.MaxDate).GetAwaiter().GetResult();

            case SecretChatFanOutOperation.SetEncryptedTyping:
                return Service.SetEncryptedTypingAsync(Input, Peer, Case.Typing).GetAwaiter().GetResult();

            default:
                throw new NotSupportedException($"Unexpected operation {Case.Operation}");
        }
    }

    /// <summary>The stored chat state each operation needs in order to reach its fan-out.</summary>
    private static ChatState StateFor(SecretChatFanOutCase @case)
    {
        return @case.Operation switch
        {
            // Only a not-yet-accepted chat can be accepted.
            SecretChatFanOutOperation.AcceptEncryption => ChatState.Waiting,
            // Any non-Discarded chat can be discarded; the generated toggle covers both.
            SecretChatFanOutOperation.DiscardEncryption => @case.DiscardFromWaiting
                ? ChatState.Waiting
                : ChatState.Active,
            _ => ChatState.Active
        };
    }

    private FakeEncryptedChatReadModel BuildChat(SecretChatIdentityFixture ids, ChatState state)
    {
        return new FakeEncryptedChatReadModel
        {
            Id = $"encrypted_chat_{ids.ChatId}",
            ChatId = ids.ChatId,
            AccessHash = ids.AccessHash,
            AdminId = ids.AdminId,
            ParticipantId = ids.ParticipantId,
            AdminPermAuthKeyId = ids.AdminPermAuthKeyId,
            // A Waiting chat has no bound receiver device yet.
            ParticipantPermAuthKeyId = state == ChatState.Waiting ? 0 : ids.ParticipantPermAuthKeyId,
            Ga = Ga,
            Gb = state == ChatState.Waiting ? [] : Gb,
            KeyFingerprint = KeyFingerprint,
            ChatState = state,
            Date = 1000,
            RandomId = 42
        };
    }

    /// <summary>
    /// A DH-valid 256-byte value tagged in its last byte, so g_a and g_b stay distinguishable while both
    /// remain inside the 2^(2048-64) safety range the server validates.
    /// </summary>
    private static byte[] DhValue(byte tag)
    {
        var value = SecretChatTestHarness.ValidDhValue();
        value[^1] = tag;

        return value;
    }
}

/// <summary>
/// FsCheck generators for Properties 6 and 7. Only the case records get custom arbitraries; every field is
/// drawn from an explicit <c>Gen</c>, so no primitive arbitrary is ever re-registered onto itself.
/// </summary>
public static class SecretChatFanOutGen
{
    /// <summary>
    /// Four distinct, non-zero identities: the two user ids and the two permanent Authorization_Key ids are
    /// generated independently of each other and of the harness constants, so correct addressing cannot be
    /// faked by accidentally matching a well-known value.
    /// </summary>
    public static Gen<SecretChatIdentityFixture> Identities =>
        from adminId in Gen.Choose(1, 1_000_000)
        from participantOffset in Gen.Choose(1, 1_000_000)
        from adminKey in Gen.Choose(1, 1_000_000)
        from participantKeyOffset in Gen.Choose(1, 1_000_000)
        from chatId in Gen.Choose(1, 1_000_000)
        from accessHash in Gen.Choose(1, int.MaxValue)
        select new SecretChatIdentityFixture(adminId,
            adminId + (long)participantOffset,
            adminKey,
            adminKey + (long)participantKeyOffset,
            chatId,
            accessHash);

    public static Gen<SecretChatFanOutOperation> Operation =>
        Gen.Elements(SecretChatFanOutOperation.RequestEncryption,
            SecretChatFanOutOperation.AcceptEncryption,
            SecretChatFanOutOperation.DiscardEncryption,
            SecretChatFanOutOperation.SendEncrypted,
            SecretChatFanOutOperation.SendEncryptedFile,
            SecretChatFanOutOperation.SendEncryptedService,
            SecretChatFanOutOperation.ReadEncryptedHistory,
            SecretChatFanOutOperation.SetEncryptedTyping);

    public static Gen<byte[]> Payload =>
        from length in Gen.Choose(SecretChatConsts.MinEncryptedPayloadLength, 96)
        from seed in Gen.Choose(0, 255)
        select BuildPayload(length, seed);

    public static Gen<SecretChatFanOutCase> FanOutCase =>
        from operation in Operation
        from ids in Identities
        from callerIsAdmin in Gen.Elements(true, false)
        from typing in Gen.Elements(true, false)
        from deleteHistory in Gen.Elements(true, false)
        from discardFromWaiting in Gen.Elements(true, false)
        from silent in Gen.Elements(true, false)
        from randomId in Gen.Choose(1, 1_000_000)
        from requestRandomId in Gen.Choose(1, 1_000_000)
        from maxDate in Gen.Choose(0, 2_000_000_000)
        from data in Payload
        select new SecretChatFanOutCase(operation, ids, callerIsAdmin, typing, deleteHistory,
            discardFromWaiting, silent, randomId, requestRandomId, maxDate, data);

    public static Gen<SecretChatTypingCase> TypingCase =>
        from ids in Identities
        from callerIsAdmin in Gen.Elements(true, false)
        from typing in Gen.Elements(true, false)
        select new SecretChatTypingCase(ids, callerIsAdmin, typing);

    private static byte[] BuildPayload(int length, int seed)
    {
        var payload = new byte[length];
        for (var i = 0; i < length; i++)
        {
            payload[i] = (byte)((seed + i * 37) % 256);
        }

        return payload;
    }
}

/// <summary>FsCheck arbitrary registration surface for Property 6.</summary>
public static class SecretChatFanOutArbitraries
{
    public static Arbitrary<SecretChatFanOutCase> FanOutCase() => Arb.From(SecretChatFanOutGen.FanOutCase);
}

/// <summary>FsCheck arbitrary registration surface for Property 7.</summary>
public static class SecretChatTypingArbitraries
{
    public static Arbitrary<SecretChatTypingCase> TypingCase() => Arb.From(SecretChatFanOutGen.TypingCase);
}
