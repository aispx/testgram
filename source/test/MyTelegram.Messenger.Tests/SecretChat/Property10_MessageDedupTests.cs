using FsCheck;
using FsCheck.Xunit;
using MyTelegram;
using MyTelegram.Domain.Aggregates.EncryptedChat;
using MyTelegram.Messenger.Services.SecretChat;
using MyTelegram.Schema;
using SchemaMessages = MyTelegram.Schema.Messages;

namespace MyTelegram.Messenger.Tests.SecretChat;

/// <summary>
/// Feature: secret-chats, Property 10: Idempotency by dedup key.
///
/// For any operation that carries a dedup key — <c>messages.requestEncryption</c> keyed by
/// (admin, random_id), <c>messages.sendEncrypted</c> / <c>sendEncryptedFile</c> / <c>sendEncryptedService</c>
/// keyed by (chat, sender, random_id), and <c>messages.reportEncryptedSpam</c> keyed by (caller, chat) —
/// repeating the request produces AT MOST ONE effect: at most one created chat, at most one stored blob, at
/// most one spam record, at most one delivery of the corresponding update, at most one qts burned on the
/// recipient device; and every repeat returns the SAME result — the same chat id and access_hash, the same
/// send date (and the same encrypted-file descriptor), or <c>boolTrue</c>.
///
/// Validates: Requirements 3.8, 6.4, 7.6, 8.5, 14.4.
///
/// <para><b>How this is tested.</b> Each generated <see cref="SecretChatDedupCase"/> picks the operation,
/// a freshly generated identity quintuple (both user ids, both permanent Authorization_Key ids, the chat id
/// and its access_hash — nothing is hard-coded to the harness constants, so the dedup key cannot be right by
/// accident), which side of the chat issues the request, the number of repeats (2..5), two DISTINCT
/// random_ids for the send key, two DISTINCT random_ids for the request key, two payload blobs and the
/// silent flag. Every case drives the REAL <see cref="SecretChatAppService"/> wired to the REAL
/// <see cref="SecretChatAccessResolver"/> and the REAL TL converters; only the transport
/// (<see cref="RecordingUpdateDispatcher"/>), the command bus and the three stores are the hand-written
/// harness fakes, whose semantics mirror the Mongo ones (insert-or-return-existing on the dedup key).</para>
///
/// <para>Three independent statements are asserted, each computed without consulting the production code:
/// (1) <b>idempotence</b> — N repeats of one dedup key collapse to a single normalized
/// <see cref="SecretChatDedupResultShape"/>, exactly one aggregate command / stored document / uploaded
/// blob, exactly one dispatched update, and a recipient qts sequence that advanced by at most one;
/// (2) <b>non-vacuity</b> — the very same case run with a DIFFERENT dedup key (a different random_id, or for
/// reportEncryptedSpam a different caller) produces two distinct effects, so the property cannot be passing
/// merely because the operation does nothing; (3) <b>key composition</b> — the send key contains the SENDER,
/// so the other participant reusing the identical random_id in the identical chat is a different key and
/// yields a second stored blob and a second delivery, each drawing from its own recipient device sequence.
/// Each property runs a minimum of 100 generated cases.</para>
///
/// <para><b>Scope.</b> This file covers the service/store half of the property. The
/// <c>reportEncryptedSpam</c> service method is a deliberate pass-through: the "at most one spam record"
/// invariant is enforced inside <see cref="EncryptedChatAggregate.ReportEncryptedChatSpam"/> (repeat reports
/// by the same caller emit no event) and is covered by MyTelegram.Domain.Tests. What is asserted here is the
/// service-level consequence: every repeat returns <c>boolTrue</c>, delivers no update, stores nothing, and
/// every command it publishes describes the SAME (chat, reporter) pair — i.e. at most one distinct spam
/// record is ever requested.</para>
/// </summary>
public class Property10_MessageDedupTests
{
    // ==============================================================================================
    // (1) Idempotence — repeating one dedup key has at most one effect and returns the same result.
    // ==============================================================================================

    [Property(Arbitrary = new[] { typeof(SecretChatDedupArbitraries) }, MaxTest = 100)]
    public void Repeating_one_dedup_key_produces_at_most_one_effect_and_the_same_result(
        SecretChatDedupCase @case)
    {
        var world = new SecretChatDedupWorld(@case);
        var because = @case.ToString();

        var results = new List<SecretChatDedupResultShape>();
        for (var repeat = 0; repeat < @case.Repeats; repeat++)
        {
            results.Add(SecretChatDedupResultShape.Of(world.InvokeWithFirstKey()));
        }

        // Every repeat answered, and every answer is byte-for-byte the same RPC result.
        results.Count.ShouldBe(@case.Repeats, because);
        results.Distinct().Count().ShouldBe(1, because);
        results[0].Kind.ShouldBe(ExpectedResultKind(@case.Operation), because);

        switch (@case.Operation)
        {
            case SecretChatDedupOperation.RequestEncryption:
                // Requirement 3.8: at most ONE chat is created for (admin, random_id) ...
                var created = world.CreateChatCommands.ShouldHaveSingleItem();
                ((long)created.ChatId).ShouldBe(results[0].Id, because);
                created.AccessHash.ShouldBe(results[0].AccessHash, because);
                created.AdminId.ShouldBe(world.CallerUserId, because);
                created.ParticipantId.ShouldBe(world.OtherUserId, because);
                created.RandomId.ShouldBe(@case.RequestRandomId, because);

                // ... the ledger row that owns the key points at exactly that chat ...
                var reserved = world.RequestLedger.FindAsync(world.CallerUserId, @case.RequestRandomId)
                    .GetAwaiter().GetResult();
                reserved.ShouldNotBeNull(because);
                ((long)reserved!.ChatId).ShouldBe(results[0].Id, because);
                reserved.AccessHash.ShouldBe(results[0].AccessHash, because);

                // ... and the request is announced to the target exactly once.
                var announced = world.Dispatcher.Dispatched.ShouldHaveSingleItem();
                announced.UserId.ShouldBe(world.OtherUserId, because);
                announced.Update.ShouldBeOfType<TUpdateEncryption>().Chat
                    .ShouldBeOfType<TEncryptedChatRequested>()
                    .Id.ShouldBe((int)results[0].Id, because);
                world.MessageStore.All.ShouldBeEmpty(because);

                break;

            case SecretChatDedupOperation.SendEncrypted:
            case SecretChatDedupOperation.SendEncryptedFile:
            case SecretChatDedupOperation.SendEncryptedService:
                // Requirements 6.4 / 7.6 / 8.5: one stored blob under the (chat, sender, random_id) key ...
                var stored = world.MessageStore.All.ShouldHaveSingleItem();
                stored.Id.ShouldBe(EncryptedMessageDocument.BuildId(@case.Ids.ChatId,
                        world.CallerUserId,
                        @case.SendRandomId),
                    because);
                stored.RandomId.ShouldBe(@case.SendRandomId, because);
                // The repeat replays the ORIGINAL date, not a fresh one.
                stored.Date.ShouldBe(results[0].Date, because);

                // ... one delivery of updateNewEncryptedMessage to the recipient's bound device ...
                var delivered = world.Dispatcher.Dispatched.ShouldHaveSingleItem();
                delivered.UserId.ShouldBe(world.OtherUserId, because);
                delivered.OnlySendToThisAuthKeyId.ShouldBe(world.OtherPermAuthKeyId, because);
                delivered.Update.ShouldBeOfType<TUpdateNewEncryptedMessage>().Qts
                    .ShouldBe(SecretChatConsts.QtsInitialValue, because);

                // ... no aggregate command, and for a file send exactly one uploaded blob is materialised.
                world.CommandBus.Published.ShouldBeEmpty(because);
                world.FileStore.StoreUploadedCallCount.ShouldBe(
                    @case.Operation == SecretChatDedupOperation.SendEncryptedFile ? 1 : 0, because);

                break;

            case SecretChatDedupOperation.ReportEncryptedSpam:
                // Requirement 14.4: repeats stay boolTrue, notify nobody, store nothing, and describe at
                // most one (chat, reporter) record (the aggregate drops the repeats — see class remarks).
                world.Dispatcher.Dispatched.ShouldBeEmpty(because);
                world.MessageStore.All.ShouldBeEmpty(because);
                world.CommandBus.Published.ShouldAllBe(c => c is ReportEncryptedChatSpamCommand);
                var record = world.SpamRecords.ShouldHaveSingleItem();
                record.ReporterId.ShouldBe(world.CallerUserId, because);
                record.AggregateId.ShouldBe(EncryptedChatId.Create(@case.Ids.ChatId).Value, because);

                break;

            default:
                throw new NotSupportedException($"Unexpected operation {@case.Operation}");
        }

        // At most one qts is burned on the recipient device, and never one on the sender's own device.
        var expectedRecipientQts = IsSend(@case.Operation)
            ? SecretChatConsts.QtsInitialValue
            : SecretChatConsts.QtsInitialValue - 1;
        world.HighestQts(world.OtherUserId, world.OtherPermAuthKeyId).ShouldBe(expectedRecipientQts, because);
        world.HighestQts(world.CallerUserId, world.CallerPermAuthKeyId)
            .ShouldBe(SecretChatConsts.QtsInitialValue - 1, because);
    }

    // ==============================================================================================
    // (2) Non-vacuity — a DIFFERENT dedup key produces a second, distinct effect.
    // ==============================================================================================

    /// <summary>
    /// The property must not hold merely because the operation is inert: running the same case again under a
    /// different dedup key (a different random_id; for reportEncryptedSpam the other participant reporting)
    /// must produce a second created chat / stored blob / spam record and a second delivery.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(SecretChatDedupArbitraries) }, MaxTest = 100)]
    public void A_different_dedup_key_produces_a_second_distinct_effect(SecretChatDedupCase @case)
    {
        var world = new SecretChatDedupWorld(@case);
        var because = @case.ToString();

        var first = SecretChatDedupResultShape.Of(world.InvokeWithFirstKey());
        var second = SecretChatDedupResultShape.Of(world.InvokeWithSecondKey());

        switch (@case.Operation)
        {
            case SecretChatDedupOperation.RequestEncryption:
                world.CreateChatCommands.Count.ShouldBe(2, because);
                world.Dispatcher.Dispatched.Count.ShouldBe(2, because);
                // Two genuinely different chats — the ids come from the id generator, not from the key.
                first.Id.ShouldNotBe(second.Id, because);
                world.CreateChatCommands.Select(c => (long)c.ChatId).Distinct().Count().ShouldBe(2, because);
                world.RequestLedger.FindAsync(world.CallerUserId, @case.SecondRequestRandomId)
                    .GetAwaiter().GetResult()!.ChatId.ShouldBe((int)second.Id, because);

                break;

            case SecretChatDedupOperation.SendEncrypted:
            case SecretChatDedupOperation.SendEncryptedFile:
            case SecretChatDedupOperation.SendEncryptedService:
                world.MessageStore.All.Count.ShouldBe(2, because);
                world.MessageStore.All.Select(d => d.Id).Distinct().Count().ShouldBe(2, because);
                world.MessageStore.All.Select(d => d.RandomId).OrderBy(r => r)
                    .ShouldBe(new[] { @case.SendRandomId, @case.SecondSendRandomId }.OrderBy(r => r), because);

                // Two deliveries drawing two consecutive values from the recipient device's sequence.
                world.Dispatcher.Dispatched.Count.ShouldBe(2, because);
                world.Dispatcher.Dispatched.Select(d => d.Qts)
                    .ShouldBe([SecretChatConsts.QtsInitialValue, SecretChatConsts.QtsInitialValue + 1],
                        because);
                world.HighestQts(world.OtherUserId, world.OtherPermAuthKeyId)
                    .ShouldBe(SecretChatConsts.QtsInitialValue + 1, because);

                if (@case.Operation == SecretChatDedupOperation.SendEncryptedFile)
                {
                    // Two distinct blobs were materialised, and the two results carry the two descriptors.
                    world.FileStore.StoreUploadedCallCount.ShouldBe(2, because);
                    first.FileId.ShouldNotBe(second.FileId, because);
                }

                break;

            case SecretChatDedupOperation.ReportEncryptedSpam:
                // A different caller is a different key: two distinct (chat, reporter) records.
                world.SpamRecords.Count.ShouldBe(2, because);
                world.SpamRecords.Select(r => r.ReporterId).OrderBy(r => r)
                    .ShouldBe(new[] { world.CallerUserId, world.OtherUserId }.OrderBy(r => r), because);
                world.SpamRecords.Select(r => r.AggregateId).Distinct()
                    .ShouldHaveSingleItem()
                    .ShouldBe(EncryptedChatId.Create(@case.Ids.ChatId).Value, because);
                world.Dispatcher.Dispatched.ShouldBeEmpty(because);
                first.ShouldBe(second, because);

                break;

            default:
                throw new NotSupportedException($"Unexpected operation {@case.Operation}");
        }
    }

    // ==============================================================================================
    // (3) Key composition — the send key contains the SENDER, not just (chat, random_id).
    // ==============================================================================================

    /// <summary>
    /// Requirement 6.4: the dedup key of a send is (chat, sender, random_id). The other participant reusing
    /// the identical random_id in the identical chat is therefore a DIFFERENT key: it stores a second blob,
    /// delivers a second update in the opposite direction, and both recipient devices — whose sequences are
    /// independent — receive the first value of their own sequence. Repeating either send afterwards is
    /// still a no-op.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(SecretChatDedupArbitraries) }, MaxTest = 100)]
    public void The_send_dedup_key_is_scoped_to_the_sender(SecretChatSendDedupCase sendCase)
    {
        var @case = sendCase.Case;
        var world = new SecretChatDedupWorld(@case);
        var because = @case.ToString();

        var fromCaller = SecretChatDedupResultShape.Of(world.InvokeWithFirstKey());
        var fromOther = SecretChatDedupResultShape.Of(world.InvokeFromOtherPartyWithFirstKey());

        // Same chat, same random_id, different sender -> two independent stored messages.
        world.MessageStore.All.Count.ShouldBe(2, because);
        world.MessageStore.All.Select(d => d.Id).OrderBy(id => id).ShouldBe(
            new[]
            {
                EncryptedMessageDocument.BuildId(@case.Ids.ChatId, world.CallerUserId, @case.SendRandomId),
                EncryptedMessageDocument.BuildId(@case.Ids.ChatId, world.OtherUserId, @case.SendRandomId)
            }.OrderBy(id => id),
            because);

        // One delivery in each direction, each carrying the first value of ITS recipient's own sequence.
        world.Dispatcher.Dispatched.Count.ShouldBe(2, because);
        world.Dispatcher.Dispatched[0].UserId.ShouldBe(world.OtherUserId, because);
        world.Dispatcher.Dispatched[1].UserId.ShouldBe(world.CallerUserId, because);
        world.Dispatcher.Dispatched.Select(d => d.Qts)
            .ShouldBe([SecretChatConsts.QtsInitialValue, SecretChatConsts.QtsInitialValue], because);

        // Repeating either send is still a no-op that replays the original result.
        SecretChatDedupResultShape.Of(world.InvokeWithFirstKey()).ShouldBe(fromCaller, because);
        SecretChatDedupResultShape.Of(world.InvokeFromOtherPartyWithFirstKey()).ShouldBe(fromOther, because);
        world.MessageStore.All.Count.ShouldBe(2, because);
        world.Dispatcher.Dispatched.Count.ShouldBe(2, because);
        world.HighestQts(world.CallerUserId, world.CallerPermAuthKeyId)
            .ShouldBe(SecretChatConsts.QtsInitialValue, because);
        world.HighestQts(world.OtherUserId, world.OtherPermAuthKeyId)
            .ShouldBe(SecretChatConsts.QtsInitialValue, because);
    }

    // ==============================================================================================
    // Worked examples — the same statements pinned down on concrete, readable cases.
    // ==============================================================================================

    [Fact]
    public void A_repeated_requestEncryption_returns_the_first_chat_id_and_access_hash()
    {
        var world = new SecretChatDedupWorld(FixedCase(SecretChatDedupOperation.RequestEncryption));

        var first = (TEncryptedChatWaiting)world.InvokeWithFirstKey();
        var second = (TEncryptedChatWaiting)world.InvokeWithFirstKey();

        second.Id.ShouldBe(first.Id);
        second.AccessHash.ShouldBe(first.AccessHash);
        second.Date.ShouldBe(first.Date);
        second.AdminId.ShouldBe(first.AdminId);
        second.ParticipantId.ShouldBe(first.ParticipantId);

        world.CreateChatCommands.ShouldHaveSingleItem();
        world.Dispatcher.Dispatched.ShouldHaveSingleItem();
    }

    [Fact]
    public void A_repeated_sendEncrypted_returns_the_original_date_and_stores_nothing_new()
    {
        var world = new SecretChatDedupWorld(FixedCase(SecretChatDedupOperation.SendEncrypted));

        var first = (SchemaMessages.TSentEncryptedMessage)world.InvokeWithFirstKey();
        var second = (SchemaMessages.TSentEncryptedMessage)world.InvokeWithFirstKey();

        second.Date.ShouldBe(first.Date);
        world.MessageStore.All.ShouldHaveSingleItem();
        world.Dispatcher.Dispatched.ShouldHaveSingleItem();
        world.HighestQts(world.OtherUserId, world.OtherPermAuthKeyId)
            .ShouldBe(SecretChatConsts.QtsInitialValue);
    }

    [Fact]
    public void A_repeated_sendEncryptedFile_returns_the_original_file_descriptor_and_uploads_once()
    {
        var world = new SecretChatDedupWorld(FixedCase(SecretChatDedupOperation.SendEncryptedFile));

        var first = (SchemaMessages.TSentEncryptedFile)world.InvokeWithFirstKey();
        var second = (SchemaMessages.TSentEncryptedFile)world.InvokeWithFirstKey();

        var firstFile = first.File.ShouldBeOfType<TEncryptedFile>();
        var secondFile = second.File.ShouldBeOfType<TEncryptedFile>();
        second.Date.ShouldBe(first.Date);
        secondFile.Id.ShouldBe(firstFile.Id);
        secondFile.AccessHash.ShouldBe(firstFile.AccessHash);
        secondFile.Size.ShouldBe(firstFile.Size);
        secondFile.KeyFingerprint.ShouldBe(firstFile.KeyFingerprint);

        world.FileStore.StoreUploadedCallCount.ShouldBe(1);
        world.MessageStore.All.ShouldHaveSingleItem();
        world.Dispatcher.Dispatched.ShouldHaveSingleItem();
    }

    [Fact]
    public void A_repeated_reportEncryptedSpam_returns_boolTrue_and_delivers_no_update()
    {
        var world = new SecretChatDedupWorld(FixedCase(SecretChatDedupOperation.ReportEncryptedSpam));

        world.InvokeWithFirstKey().ShouldBeOfType<TBoolTrue>();
        world.InvokeWithFirstKey().ShouldBeOfType<TBoolTrue>();
        world.InvokeWithFirstKey().ShouldBeOfType<TBoolTrue>();

        world.Dispatcher.Dispatched.ShouldBeEmpty();
        world.MessageStore.All.ShouldBeEmpty();
        world.SpamRecords.ShouldHaveSingleItem().ReporterId.ShouldBe(world.CallerUserId);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static bool IsSend(SecretChatDedupOperation operation)
    {
        return operation is SecretChatDedupOperation.SendEncrypted
            or SecretChatDedupOperation.SendEncryptedFile
            or SecretChatDedupOperation.SendEncryptedService;
    }

    /// <summary>The TL constructor each operation answers with, stated independently of the service.</summary>
    private static string ExpectedResultKind(SecretChatDedupOperation operation)
    {
        return operation switch
        {
            SecretChatDedupOperation.RequestEncryption => "encryptedChatWaiting",
            SecretChatDedupOperation.SendEncrypted => "sentEncryptedMessage",
            SecretChatDedupOperation.SendEncryptedFile => "sentEncryptedFile",
            SecretChatDedupOperation.SendEncryptedService => "sentEncryptedMessage",
            SecretChatDedupOperation.ReportEncryptedSpam => "boolTrue",
            _ => throw new NotSupportedException($"Unexpected operation {operation}")
        };
    }

    private static SecretChatDedupCase FixedCase(SecretChatDedupOperation operation)
    {
        return new SecretChatDedupCase(operation,
            new SecretChatDedupIdentity(AdminId: 3001,
                ParticipantId: 4002,
                AdminPermAuthKeyId: 5003,
                ParticipantPermAuthKeyId: 6004,
                ChatId: 17,
                AccessHash: 123456789),
            CallerIsAdmin: true,
            Repeats: 2,
            RequestRandomId: 4242,
            SecondRequestRandomId: 4243,
            SendRandomId: 990001,
            SecondSendRandomId: 990002,
            Data: SecretChatTestHarness.Payload(9, 8, 7, 6, 5),
            SecondData: SecretChatTestHarness.Payload(1, 2, 3),
            Silent: false);
    }
}

// ---- Generated cases, result shape and the wired world -------------------------------------------

/// <summary>The secret-chat operations that carry a dedup key.</summary>
public enum SecretChatDedupOperation
{
    RequestEncryption,
    SendEncrypted,
    SendEncryptedFile,
    SendEncryptedService,
    ReportEncryptedSpam
}

/// <summary>The identities a dedup key is composed from, generated independently per case.</summary>
public sealed record SecretChatDedupIdentity(
    long AdminId,
    long ParticipantId,
    long AdminPermAuthKeyId,
    long ParticipantPermAuthKeyId,
    int ChatId,
    long AccessHash);

/// <summary>
/// One generated idempotency case: the operation, the identities, which side issues it, how many times the
/// same key is replayed, and two DISTINCT random_ids per key kind plus two payloads for the non-vacuity run.
/// </summary>
public sealed record SecretChatDedupCase(
    SecretChatDedupOperation Operation,
    SecretChatDedupIdentity Ids,
    bool CallerIsAdmin,
    int Repeats,
    int RequestRandomId,
    int SecondRequestRandomId,
    long SendRandomId,
    long SecondSendRandomId,
    byte[] Data,
    byte[] SecondData,
    bool Silent)
{
    public override string ToString()
    {
        return $"DedupCase(op={Operation}, admin={Ids.AdminId}/{Ids.AdminPermAuthKeyId}, " +
               $"participant={Ids.ParticipantId}/{Ids.ParticipantPermAuthKeyId}, chat={Ids.ChatId}, " +
               $"callerIsAdmin={CallerIsAdmin}, repeats={Repeats}, requestRandomId={RequestRandomId}, " +
               $"sendRandomId={SendRandomId}, silent={Silent})";
    }
}

/// <summary>A dedup case whose operation is one of the three send methods (Property 10, part 3).</summary>
public sealed record SecretChatSendDedupCase(SecretChatDedupCase Case)
{
    public override string ToString() => Case.ToString();
}

/// <summary>
/// The identity of an RPC result, normalized across the constructors the dedup-carrying operations answer
/// with, so "the repeat returned the same result" is one structural comparison.
/// </summary>
public sealed record SecretChatDedupResultShape(
    string Kind,
    long Id,
    long AccessHash,
    int Date,
    long AdminId,
    long ParticipantId,
    long FileId,
    long FileAccessHash,
    long FileSize)
{
    public static SecretChatDedupResultShape Of(object result)
    {
        switch (result)
        {
            case TEncryptedChatWaiting waiting:
                return new SecretChatDedupResultShape("encryptedChatWaiting", waiting.Id, waiting.AccessHash,
                    waiting.Date, waiting.AdminId, waiting.ParticipantId, 0, 0, 0);

            case SchemaMessages.TSentEncryptedFile sentFile:
                var file = sentFile.File as TEncryptedFile;

                return new SecretChatDedupResultShape("sentEncryptedFile", 0, 0, sentFile.Date, 0, 0,
                    file?.Id ?? 0, file?.AccessHash ?? 0, file?.Size ?? 0);

            case SchemaMessages.TSentEncryptedMessage sent:
                return new SecretChatDedupResultShape("sentEncryptedMessage", 0, 0, sent.Date, 0, 0, 0, 0, 0);

            case TBoolTrue:
                return new SecretChatDedupResultShape("boolTrue", 0, 0, 0, 0, 0, 0, 0, 0);

            default:
                throw new NotSupportedException($"Unexpected RPC result {result.GetType().Name}");
        }
    }
}

/// <summary>The (chat, reporter) pair a published spam command describes.</summary>
public sealed record SecretChatSpamRecord(string AggregateId, long ReporterId);

/// <summary>
/// One fully wired secret-chat world for a generated dedup case: the REAL
/// <see cref="SecretChatAppService"/> over the REAL <see cref="SecretChatAccessResolver"/>, with the harness
/// fakes standing in for the transport, the command bus and the three stores.
/// </summary>
internal sealed class SecretChatDedupWorld
{
    /// <summary>Client-chosen upload id used by the sendEncryptedFile cases.</summary>
    private const long ClientFileId = 770099;

    private const int DeclaredParts = 1;
    private const int FileKeyFingerprint = 4242;

    /// <summary>First chat id the fake id generator hands out (deliberately not the generated chat id).</summary>
    private const int FirstAllocatedChatId = 100;

    public SecretChatDedupWorld(SecretChatDedupCase @case)
    {
        Case = @case;
        Ids = @case.Ids;
        CallerUserId = @case.CallerIsAdmin ? Ids.AdminId : Ids.ParticipantId;
        CallerPermAuthKeyId = @case.CallerIsAdmin ? Ids.AdminPermAuthKeyId : Ids.ParticipantPermAuthKeyId;
        OtherUserId = @case.CallerIsAdmin ? Ids.ParticipantId : Ids.AdminId;
        OtherPermAuthKeyId = @case.CallerIsAdmin ? Ids.ParticipantPermAuthKeyId : Ids.AdminPermAuthKeyId;

        QueryProcessor.Users[Ids.AdminId] = FakeUser.Create(Ids.AdminId);
        QueryProcessor.Users[Ids.ParticipantId] = FakeUser.Create(Ids.ParticipantId);
        QueryProcessor.Chats[Ids.ChatId] = BuildActiveChat();

        // Uploaded parts for both sides, so a file send succeeds whichever party issues it.
        FileStore.Parts[(Ids.AdminId, ClientFileId)] = [[3, 1, 4, 1, 5]];
        FileStore.Parts[(Ids.ParticipantId, ClientFileId)] = [[2, 7, 1, 8]];

        Input = SecretChatTestHarness.Input(CallerUserId, CallerPermAuthKeyId);
        OtherInput = SecretChatTestHarness.Input(OtherUserId, OtherPermAuthKeyId);
        Peer = SecretChatTestHarness.InputChat(Ids.AccessHash, Ids.ChatId);

        Service = new SecretChatAppService(CommandBus,
            QueryProcessor,
            new FakeIdGenerator(FirstAllocatedChatId),
            new FakeBlockCacheAppService(),
            new SecretChatAccessResolver(QueryProcessor),
            Dispatcher,
            MessageStore,
            RequestLedger,
            FileStore,
            SecretChatTestHarness.ChatConverters(),
            SecretChatTestHarness.MessageConverters(),
            SecretChatTestHarness.FileConverters());
    }

    public SecretChatDedupCase Case { get; }
    public SecretChatDedupIdentity Ids { get; }
    public long CallerUserId { get; }
    public long CallerPermAuthKeyId { get; }
    public long OtherUserId { get; }
    public long OtherPermAuthKeyId { get; }

    /// <summary>A DH-valid g_a for requestEncryption; also the g_a stored on the existing chat.</summary>
    public byte[] Ga { get; } = SecretChatTestHarness.ValidDhValue();

    public FakeQueryProcessor QueryProcessor { get; } = new();
    public RecordingCommandBus CommandBus { get; } = new();
    public RecordingUpdateDispatcher Dispatcher { get; } = new();
    public InMemorySecretChatMessageStore MessageStore { get; } = new();
    public InMemorySecretChatRequestLedger RequestLedger { get; } = new();
    public InMemoryEncryptedFileStore FileStore { get; } = new();
    public SecretChatAppService Service { get; }
    public TestRequestInput Input { get; }
    public TestRequestInput OtherInput { get; }
    public IInputEncryptedChat Peer { get; }

    public IReadOnlyList<CreateEncryptedChatCommand> CreateChatCommands =>
        CommandBus.Published.OfType<CreateEncryptedChatCommand>().ToList();

    /// <summary>The DISTINCT (chat, reporter) pairs the published spam commands describe.</summary>
    public IReadOnlyList<SecretChatSpamRecord> SpamRecords =>
        CommandBus.Published.OfType<ReportEncryptedChatSpamCommand>()
            .Select(c => new SecretChatSpamRecord(c.AggregateId.Value, c.ReporterId))
            .Distinct()
            .ToList();

    public int HighestQts(long userId, long permAuthKeyId)
    {
        return MessageStore.GetHighestQtsAsync(userId, permAuthKeyId).GetAwaiter().GetResult();
    }

    /// <summary>The case's primary dedup key, issued by the calling side.</summary>
    public object InvokeWithFirstKey()
    {
        return Invoke(Input, Case.RequestRandomId, Case.SendRandomId, Case.Data);
    }

    /// <summary>
    /// A DIFFERENT dedup key: a different random_id for the keyed operations, and — since the
    /// reportEncryptedSpam key is (caller, chat) — the other participant for the spam report.
    /// </summary>
    public object InvokeWithSecondKey()
    {
        return Case.Operation == SecretChatDedupOperation.ReportEncryptedSpam
            ? Invoke(OtherInput, Case.SecondRequestRandomId, Case.SecondSendRandomId, Case.SecondData)
            : Invoke(Input, Case.SecondRequestRandomId, Case.SecondSendRandomId, Case.SecondData);
    }

    /// <summary>The primary random_id replayed by the OTHER participant — a different send key.</summary>
    public object InvokeFromOtherPartyWithFirstKey()
    {
        return Invoke(OtherInput, Case.RequestRandomId, Case.SendRandomId, Case.Data);
    }

    private object Invoke(TestRequestInput input, int requestRandomId, long sendRandomId, byte[] data)
    {
        switch (Case.Operation)
        {
            case SecretChatDedupOperation.RequestEncryption:
                var targetUserId = input.UserId == Ids.AdminId ? Ids.ParticipantId : Ids.AdminId;

                return Service
                    .RequestEncryptionAsync(input,
                        new TInputUser { UserId = targetUserId, AccessHash = 0 },
                        requestRandomId,
                        Ga)
                    .GetAwaiter().GetResult();

            case SecretChatDedupOperation.SendEncrypted:
                return Service.SendEncryptedAsync(input, Peer, sendRandomId, data, Case.Silent)
                    .GetAwaiter().GetResult();

            case SecretChatDedupOperation.SendEncryptedFile:
                return Service.SendEncryptedFileAsync(input,
                        Peer,
                        sendRandomId,
                        data,
                        new TInputEncryptedFileUploaded
                        {
                            Id = ClientFileId,
                            Parts = DeclaredParts,
                            KeyFingerprint = FileKeyFingerprint,
                            Md5Checksum = string.Empty
                        },
                        Case.Silent)
                    .GetAwaiter().GetResult();

            case SecretChatDedupOperation.SendEncryptedService:
                return Service.SendEncryptedServiceAsync(input, Peer, sendRandomId, data)
                    .GetAwaiter().GetResult();

            case SecretChatDedupOperation.ReportEncryptedSpam:
                return Service.ReportEncryptedSpamAsync(input, Peer).GetAwaiter().GetResult();

            default:
                throw new NotSupportedException($"Unexpected operation {Case.Operation}");
        }
    }

    /// <summary>An established chat, so the send and spam paths reach their dedup key.</summary>
    private FakeEncryptedChatReadModel BuildActiveChat()
    {
        return new FakeEncryptedChatReadModel
        {
            Id = $"encrypted_chat_{Ids.ChatId}",
            ChatId = Ids.ChatId,
            AccessHash = Ids.AccessHash,
            AdminId = Ids.AdminId,
            ParticipantId = Ids.ParticipantId,
            AdminPermAuthKeyId = Ids.AdminPermAuthKeyId,
            ParticipantPermAuthKeyId = Ids.ParticipantPermAuthKeyId,
            Ga = Ga,
            Gb = SecretChatTestHarness.ValidDhValue(),
            KeyFingerprint = 987_654_321_012L,
            ChatState = ChatState.Active,
            Date = 1000,
            RandomId = 42
        };
    }
}

/// <summary>
/// FsCheck generators for Property 10. Only the case records get custom arbitraries; every field is drawn
/// from an explicit <c>Gen</c>, so no primitive arbitrary is ever re-registered onto itself.
/// </summary>
public static class SecretChatDedupGen
{
    /// <summary>
    /// Five independent, non-zero identities: both user ids, both permanent Authorization_Key ids and the
    /// chat id (plus its access_hash), generated apart from each other and from the harness constants, so a
    /// dedup key that silently drops one of its components cannot survive.
    /// </summary>
    public static Gen<SecretChatDedupIdentity> Identities =>
        from adminId in Gen.Choose(1, 1_000_000)
        from participantOffset in Gen.Choose(1, 1_000_000)
        from adminKey in Gen.Choose(1, 1_000_000)
        from participantKeyOffset in Gen.Choose(1, 1_000_000)
        from chatId in Gen.Choose(1, 1_000_000)
        from accessHash in Gen.Choose(1, int.MaxValue)
        select new SecretChatDedupIdentity(adminId,
            adminId + (long)participantOffset,
            adminKey,
            adminKey + (long)participantKeyOffset,
            chatId,
            accessHash);

    public static Gen<SecretChatDedupOperation> Operation =>
        Gen.Elements(SecretChatDedupOperation.RequestEncryption,
            SecretChatDedupOperation.SendEncrypted,
            SecretChatDedupOperation.SendEncryptedFile,
            SecretChatDedupOperation.SendEncryptedService,
            SecretChatDedupOperation.ReportEncryptedSpam);

    public static Gen<SecretChatDedupOperation> SendOperation =>
        Gen.Elements(SecretChatDedupOperation.SendEncrypted,
            SecretChatDedupOperation.SendEncryptedFile,
            SecretChatDedupOperation.SendEncryptedService);

    public static Gen<byte[]> Payload =>
        from length in Gen.Choose(SecretChatConsts.MinEncryptedPayloadLength, 96)
        from seed in Gen.Choose(0, 255)
        select BuildPayload(length, seed);

    public static Gen<SecretChatDedupCase> DedupCase => CaseWith(Operation);

    public static Gen<SecretChatSendDedupCase> SendDedupCase =>
        from @case in CaseWith(SendOperation)
        select new SecretChatSendDedupCase(@case);

    /// <summary>
    /// The two random_ids of each kind are generated as a value plus a strictly positive offset, so the
    /// "different key" run is guaranteed to use a genuinely different key.
    /// </summary>
    private static Gen<SecretChatDedupCase> CaseWith(Gen<SecretChatDedupOperation> operationGen) =>
        from operation in operationGen
        from ids in Identities
        from callerIsAdmin in Gen.Elements(true, false)
        from repeats in Gen.Choose(2, 5)
        from requestRandomId in Gen.Choose(1, 1_000_000)
        from requestRandomIdOffset in Gen.Choose(1, 1_000_000)
        from sendRandomId in Gen.Choose(1, 1_000_000)
        from sendRandomIdOffset in Gen.Choose(1, 1_000_000)
        from data in Payload
        from secondData in Payload
        from silent in Gen.Elements(true, false)
        select new SecretChatDedupCase(operation,
            ids,
            callerIsAdmin,
            repeats,
            requestRandomId,
            requestRandomId + requestRandomIdOffset,
            sendRandomId,
            sendRandomId + (long)sendRandomIdOffset,
            data,
            secondData,
            silent);

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

/// <summary>FsCheck arbitrary registration surface for Property 10.</summary>
public static class SecretChatDedupArbitraries
{
    public static Arbitrary<SecretChatDedupCase> DedupCase() => Arb.From(SecretChatDedupGen.DedupCase);

    public static Arbitrary<SecretChatSendDedupCase> SendDedupCase() =>
        Arb.From(SecretChatDedupGen.SendDedupCase);
}
