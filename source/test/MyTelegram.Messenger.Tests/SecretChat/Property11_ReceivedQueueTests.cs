using EventFlow.Queries;
using FsCheck;
using FsCheck.Xunit;
using MyTelegram;
using MyTelegram.Messenger.Services.SecretChat;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.SecretChat;

/// <summary>
/// Feature: secret-chats, Property 11: receivedQueue acknowledgement and returned random_ids — and
/// Property 12: receivedQueue max_qts validation.
///
/// <para><b>Property 11.</b> For any Authorization_Key and its queue of secret updates: with
/// <c>max_qts</c> &lt;= the highest assigned qts, exactly the not-yet-acknowledged updates with
/// <c>qts &lt;= max_qts</c> are acknowledged, their push is cancelled, and the returned vector contains the
/// <c>random_id</c> of each newly acknowledged message exactly once and contains no <c>random_id</c> for
/// non-messages; when <c>max_qts</c> covers only already-acknowledged updates, an empty vector is returned
/// with no re-acknowledgement.
/// <b>Validates: Requirements 13.1, 13.3, 13.4, 13.6.</b></para>
///
/// <para><b>Property 12.</b> For any Authorization_Key, if <c>max_qts</c> is greater than the highest qts
/// assigned to that key — and for a key with no assignments the highest is treated as
/// <c>QtsInitialValue - 1</c> — <c>MAX_QTS_INVALID</c> is returned and no update is acknowledged.
/// <b>Validates: Requirements 13.2, 13.5.</b></para>
///
/// <para><b>How this is tested — two levels, both against production code.</b></para>
///
/// <para><i>(a) RPC level, in-memory.</i> The FsCheck properties drive the REAL
/// <see cref="SecretChatAppService"/> wired to the REAL <see cref="SecretChatAccessResolver"/> over the
/// in-memory store. <see cref="ReceivedQueueAckCase"/> generates a sequence of secret-chat operations —
/// admin-&gt;participant text messages, admin-&gt;participant service messages, participant-&gt;admin
/// messages (which land in the OTHER device's box), <c>readEncryptedHistory</c>,
/// <c>setEncryptedTyping(true)</c> and <c>setEncryptedTyping(false)</c> — plus a selector that picks an
/// arbitrary <c>max_qts</c> in the valid range <c>[QtsInitialValue - 1 .. highest]</c>. The expected qts of
/// every delivered message and the expected acknowledged subset are computed independently of the store,
/// straight from the property statement ("the j-th message delivered to a device carries
/// <c>QtsInitialValue + j - 1</c>"), and never read back from the production code. The property asserts the
/// returned vector element-for-element (ordered by qts), asserts it holds no duplicates, asserts the push
/// is cancelled by re-reading the device's pending update box (<c>GetForDifferenceAsync</c>), asserts a
/// repeat call — and any call with a smaller <c>max_qts</c> — returns an empty vector, asserts a final
/// full-range call returns exactly the remainder so that the two calls together cover every message exactly
/// once, and asserts the peer device's box is untouched. Non-message updates are counted on the transport
/// (they are dispatched, and they carry no qts), and the total number of returned <c>random_id</c>s equals
/// the number of MESSAGES only — so a non-message update leaking a <c>random_id</c> into the queue fails.
/// <see cref="MaxQtsOverflowCase"/> generates the number of delivered messages (including zero, i.e. a
/// brand new Authorization_Key) and a strictly positive excess over the highest assigned qts.</para>
///
/// <para><i>(b) Store level, real MongoDB.</i> Acknowledgement is a per-row conditional update, so its
/// exactness under concurrency cannot be shown against an in-memory fake. Three
/// <see cref="RequiresMongoDbFactAttribute"/> facts run the REAL <see cref="SecretChatMessageStore"/>
/// against a REAL <c>mongod</c> started by <see cref="EmbeddedMongoServer"/> (skipping cleanly when no
/// <c>mongod</c> binary is present). Generated cases are sampled with <c>Gen.Sample</c> inside the facts,
/// since <c>[Property]</c> and <c>[RequiresMongoDbFact]</c> cannot be combined. They cover: the per-row
/// semantics with pre-acknowledged rows, rows whose qts was never assigned and rows belonging to a second
/// device of the same user; a CONCURRENT double acknowledgement where two <c>ReceivedQueueAsync</c> calls
/// race with the same <c>max_qts</c> and must together return each <c>random_id</c> exactly once, never
/// twice; and the full RPC path (send -&gt; receivedQueue -&gt; MAX_QTS_INVALID) over real persistence.</para>
/// </summary>
public class Property11_ReceivedQueueTests
{
    /// <summary>Generated cases for the Mongo-backed facts (they cannot carry <c>[Property]</c>).</summary>
    private const int MongoGeneratedCases = 100;

    /// <summary>Cases for the heavier facts (each one performs a full send/ack round trip).</summary>
    private const int HeavyMongoGeneratedCases = 40;

    /// <summary>FsCheck size parameter for <c>Gen.Sample</c>; every generator here is size-independent.</summary>
    private const int SampleSize = 50;

    // ==========================================================================================
    // Property 11 — acknowledgement and returned random_ids (Requirements 13.1, 13.3, 13.4, 13.6).
    // ==========================================================================================

    /// <summary>
    /// Requirements 13.1, 13.3, 13.4, 13.6: <c>messages.receivedQueue(max_qts)</c> acknowledges exactly the
    /// not-yet-acknowledged updates of the CALLING Authorization_Key with <c>qts &lt;= max_qts</c>, returns
    /// their <c>random_id</c>s (each exactly once, no <c>random_id</c> for updates that are not messages),
    /// cancels their push, and returns an empty vector when nothing new falls in range.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(ReceivedQueueArbitraries) }, MaxTest = 100)]
    public void ReceivedQueue_acknowledges_exactly_the_unacked_messages_up_to_max_qts(ReceivedQueueAckCase @case)
    {
        var store = new InMemorySecretChatMessageStore();
        var fixture = BuildFixture(store, chatId: 5, identityBase: 3_000_000);
        var because = @case.ToString();

        // Expected boxes, derived from the property statement alone: the j-th message delivered to a device
        // carries QtsInitialValue + j - 1, and only messages enter the device's update box at all.
        var participantBox = new List<(int Qts, long RandomId)>();
        var adminBox = new List<(int Qts, long RandomId)>();
        var nonMessageUpdates = 0;

        for (var i = 0; i < @case.Ops.Length; i++)
        {
            var randomId = 5_000_000L + i;
            switch (@case.Ops[i])
            {
                case ReceivedQueueOpKind.AdminMessage:
                    fixture.Service
                        .SendEncryptedAsync(fixture.Admin, fixture.Peer, randomId, new byte[] { 1, (byte)i },
                            silent: false)
                        .GetAwaiter().GetResult();
                    participantBox.Add((SecretChatConsts.QtsInitialValue + participantBox.Count, randomId));
                    break;

                case ReceivedQueueOpKind.AdminServiceMessage:
                    fixture.Service
                        .SendEncryptedServiceAsync(fixture.Admin, fixture.Peer, randomId, new byte[] { 2, (byte)i })
                        .GetAwaiter().GetResult();
                    participantBox.Add((SecretChatConsts.QtsInitialValue + participantBox.Count, randomId));
                    break;

                case ReceivedQueueOpKind.ParticipantMessage:
                    // Travels the other way: it belongs to the ADMIN's box and must never be acknowledged by
                    // the participant's receivedQueue.
                    fixture.Service
                        .SendEncryptedAsync(fixture.Participant, fixture.Peer, randomId, new byte[] { 3, (byte)i },
                            silent: false)
                        .GetAwaiter().GetResult();
                    adminBox.Add((SecretChatConsts.QtsInitialValue + adminBox.Count, randomId));
                    break;

                case ReceivedQueueOpKind.ReadHistory:
                    // A non-message update: dispatched, but carries no qts and no random_id.
                    fixture.Service.ReadEncryptedHistoryAsync(fixture.Admin, fixture.Peer, maxDate: 1000 + i)
                        .GetAwaiter().GetResult();
                    nonMessageUpdates++;
                    break;

                case ReceivedQueueOpKind.TypingOn:
                    fixture.Service.SetEncryptedTypingAsync(fixture.Admin, fixture.Peer, typing: true)
                        .GetAwaiter().GetResult();
                    nonMessageUpdates++;
                    break;

                case ReceivedQueueOpKind.TypingOff:
                    // Produces no update at all.
                    fixture.Service.SetEncryptedTypingAsync(fixture.Admin, fixture.Peer, typing: false)
                        .GetAwaiter().GetResult();
                    break;

                default:
                    throw new NotSupportedException($"Unhandled op {@case.Ops[i]}");
            }
        }

        // The non-message operations really did produce updates — they simply never enter the qts box.
        fixture.Dispatcher.Dispatched.Count(d => d.Qts == null).ShouldBe(nonMessageUpdates, because);
        fixture.Dispatcher.Dispatched.Count(d => d.Qts != null)
            .ShouldBe(participantBox.Count + adminBox.Count, because);

        var highest = SecretChatConsts.QtsInitialValue - 1 + participantBox.Count;
        store.GetHighestQtsAsync(fixture.ParticipantUserId, fixture.ParticipantKeyId).GetAwaiter().GetResult()
            .ShouldBe(highest, because);

        // An arbitrary max_qts inside the valid range [QtsInitialValue - 1 .. highest].
        var maxQts = @case.MaxQtsSelector % (highest + 1);

        // Requirement 13.3/13.4: exactly the unacked messages with qts <= max_qts, each random_id once.
        var expectedFirst = participantBox.Where(m => m.Qts <= maxQts).Select(m => m.RandomId).ToList();
        var first = fixture.Service.ReceivedQueueAsync(fixture.Participant, maxQts).GetAwaiter().GetResult();

        first.ToList().ShouldBe(expectedFirst, because);
        first.Distinct().Count().ShouldBe(first.Count, because);

        // Requirement 13.6: the push of every acknowledged update is cancelled — the device's pending box
        // holds exactly the messages that were NOT acknowledged.
        var pending = store
            .GetForDifferenceAsync(fixture.ParticipantUserId, fixture.ParticipantKeyId,
                SecretChatConsts.QtsInitialValue - 1, 0)
            .GetAwaiter().GetResult();
        pending.Select(d => d.RandomId).ShouldBe(participantBox.Where(m => m.Qts > maxQts).Select(m => m.RandomId),
            because);

        // Requirement 13.4: repeating the same call (and any call covering only already-acknowledged
        // updates) returns an empty vector and re-acknowledges nothing.
        fixture.Service.ReceivedQueueAsync(fixture.Participant, maxQts).GetAwaiter().GetResult()
            .ShouldBeEmpty(because);
        fixture.Service
            .ReceivedQueueAsync(fixture.Participant, Math.Max(SecretChatConsts.QtsInitialValue - 1, maxQts - 1))
            .GetAwaiter().GetResult()
            .ShouldBeEmpty(because);

        // The remainder is still acknowledgeable, and the two calls together cover every delivered message
        // exactly once (no message acknowledged twice, none lost).
        var rest = fixture.Service.ReceivedQueueAsync(fixture.Participant, highest).GetAwaiter().GetResult();
        rest.ToList().ShouldBe(participantBox.Where(m => m.Qts > maxQts).Select(m => m.RandomId).ToList(), because);
        first.Concat(rest).OrderBy(id => id)
            .ShouldBe(participantBox.Select(m => m.RandomId).OrderBy(id => id), because);
        (first.Count + rest.Count).ShouldBe(participantBox.Count, because);
        fixture.Service.ReceivedQueueAsync(fixture.Participant, highest).GetAwaiter().GetResult()
            .ShouldBeEmpty(because);

        // The peer Authorization_Key's queue is completely unaffected by the participant's acknowledgements.
        var adminHighest = SecretChatConsts.QtsInitialValue - 1 + adminBox.Count;
        fixture.Service.ReceivedQueueAsync(fixture.Admin, adminHighest).GetAwaiter().GetResult()
            .ToList().ShouldBe(adminBox.Select(m => m.RandomId).ToList(), because);
    }

    // ==========================================================================================
    // Property 12 — max_qts validation (Requirements 13.2, 13.5).
    // ==========================================================================================

    /// <summary>
    /// Requirements 13.2, 13.5: any <c>max_qts</c> strictly greater than the highest qts assigned to the
    /// calling Authorization_Key is rejected with <c>MAX_QTS_INVALID</c> and acknowledges nothing — which is
    /// proved by acknowledging the whole range afterwards and getting every <c>random_id</c> back. For a key
    /// that was never assigned a qts the highest is <c>QtsInitialValue - 1</c>, so the generated case with
    /// zero messages pins the fresh-key boundary.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(ReceivedQueueArbitraries) }, MaxTest = 100)]
    public void ReceivedQueue_rejects_a_max_qts_above_the_highest_assigned_value(MaxQtsOverflowCase @case)
    {
        var store = new InMemorySecretChatMessageStore();
        var fixture = BuildFixture(store, chatId: 6, identityBase: 4_000_000);
        var because = @case.ToString();

        var randomIds = new List<long>();
        for (var i = 0; i < @case.MessageCount; i++)
        {
            var randomId = 6_000_000L + i;
            fixture.Service
                .SendEncryptedAsync(fixture.Admin, fixture.Peer, randomId, new byte[] { 7, (byte)i }, silent: false)
                .GetAwaiter().GetResult();
            randomIds.Add(randomId);
        }

        // Highest assigned qts, computed independently: QtsInitialValue - 1 for a key with no assignments.
        var highest = SecretChatConsts.QtsInitialValue - 1 + @case.MessageCount;
        store.GetHighestQtsAsync(fixture.ParticipantUserId, fixture.ParticipantKeyId).GetAwaiter().GetResult()
            .ShouldBe(highest, because);

        var ex = Should.Throw<RpcException>(() => fixture.Service
            .ReceivedQueueAsync(fixture.Participant, highest + @case.Excess)
            .GetAwaiter().GetResult());
        ex.RpcError.ShouldBe(RpcErrors.RpcErrors400.MaxQtsInvalid, because);

        // Nothing was acknowledged by the rejected call: the whole queue is still pending...
        store.GetForDifferenceAsync(fixture.ParticipantUserId, fixture.ParticipantKeyId,
                SecretChatConsts.QtsInitialValue - 1, 0)
            .GetAwaiter().GetResult()
            .Select(d => d.RandomId).ShouldBe(randomIds, because);

        // ... and the boundary value max_qts == highest is accepted and returns every random_id.
        fixture.Service.ReceivedQueueAsync(fixture.Participant, highest).GetAwaiter().GetResult()
            .ToList().ShouldBe(randomIds, because);
    }

    /// <summary>
    /// Requirement 13.5 at its sharpest boundary: an Authorization_Key that was never assigned a qts has a
    /// highest of <c>QtsInitialValue - 1</c>, so <c>max_qts == QtsInitialValue</c> — the value the FIRST
    /// message would ever carry — is already invalid, while <c>max_qts == QtsInitialValue - 1</c> is valid
    /// and simply acknowledges nothing.
    /// </summary>
    [Fact]
    public void A_brand_new_authorization_key_rejects_max_qts_equal_to_the_initial_value()
    {
        var store = new InMemorySecretChatMessageStore();
        var fixture = BuildFixture(store, chatId: 7, identityBase: 5_000_000);

        store.GetHighestQtsAsync(fixture.ParticipantUserId, fixture.ParticipantKeyId).GetAwaiter().GetResult()
            .ShouldBe(SecretChatConsts.QtsInitialValue - 1);

        // max_qts == QtsInitialValue - 1: in range, nothing to acknowledge.
        fixture.Service
            .ReceivedQueueAsync(fixture.Participant, SecretChatConsts.QtsInitialValue - 1)
            .GetAwaiter().GetResult()
            .ShouldBeEmpty();

        // max_qts == QtsInitialValue: already above the highest assigned value for a fresh key.
        Should.Throw<RpcException>(() => fixture.Service
                .ReceivedQueueAsync(fixture.Participant, SecretChatConsts.QtsInitialValue)
                .GetAwaiter().GetResult())
            .RpcError.ShouldBe(RpcErrors.RpcErrors400.MaxQtsInvalid);
    }

    // ==========================================================================================
    // Store level — the real Mongo-backed store (per-row acknowledgement, concurrency, RPC path).
    // ==========================================================================================

    /// <summary>
    /// Requirements 13.3, 13.4, 13.6 at the persistence level: the real <see cref="SecretChatMessageStore"/>
    /// acknowledges exactly the rows of the addressed device that are unacknowledged and carry a qts in
    /// <c>(0 .. max_qts]</c>. Each generated case seeds a device box, pre-acknowledges an arbitrary prefix,
    /// adds rows whose qts was never assigned (a crash between insert and allocation) and rows belonging to a
    /// SECOND device of the same user, and then checks that a call with an arbitrary in-range
    /// <c>max_qts</c> returns exactly the newly acknowledged <c>random_id</c>s — once each, in qts order —
    /// while the pre-acknowledged rows, the unassigned rows and the other device stay untouched.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task Real_store_acknowledges_exactly_the_unacked_rows_of_the_addressed_device()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new SecretChatMessageStore(mongo.Database);
        var cases = Sample(ReceivedQueueArbitraries.StoreCase().Generator, MongoGeneratedCases);

        for (var i = 0; i < cases.Count; i++)
        {
            var @case = cases[i];
            var userId = 2_100_000L + i;
            const long keyId = 77;
            const long otherKeyId = 78;
            var chatId = 900_000L + i;
            var randomIdBase = 1_000_000L * (i + 1);
            var because = $"case #{i} {@case}";

            // The device's own box: qts assigned exactly as the production send path does it.
            var box = new List<(int Qts, long RandomId)>();
            for (var n = 0; n < @case.MessageCount; n++)
            {
                var randomId = randomIdBase + n;
                var qts = await SeedAsync(store, chatId, userId, keyId, randomId, assignQts: true);
                box.Add((qts, randomId));
            }

            // Rows without an assigned qts are not part of the update box and are never acknowledged.
            for (var n = 0; n < @case.UnassignedCount; n++)
            {
                await SeedAsync(store, chatId, userId, keyId, randomIdBase + 500 + n, assignQts: false);
            }

            // A second Authorization_Key of the SAME user: never touched by this key's acknowledgements.
            for (var n = 0; n < @case.OtherDeviceCount; n++)
            {
                await SeedAsync(store, chatId, userId, otherKeyId, randomIdBase + 900 + n, assignQts: true);
            }

            var highest = SecretChatConsts.QtsInitialValue - 1 + @case.MessageCount;
            (await store.GetHighestQtsAsync(userId, keyId)).ShouldBe(highest, because);

            // Pre-acknowledge an arbitrary prefix of the queue.
            var preAckMaxQts = SecretChatConsts.QtsInitialValue - 1
                               + Math.Min(@case.PreAckedPrefix, @case.MessageCount);
            var preAcked = await store.AckAsync(userId, keyId, preAckMaxQts);
            preAcked.ShouldBe(box.Where(m => m.Qts <= preAckMaxQts).Select(m => m.RandomId), because);

            // The acknowledgement under test: only rows above the pre-acknowledged prefix come back.
            var maxQts = @case.MaxQtsSelector % (highest + 1);
            var expected = box.Where(m => m.Qts > preAckMaxQts && m.Qts <= maxQts).Select(m => m.RandomId).ToList();
            var acked = await store.AckAsync(userId, keyId, maxQts);

            acked.ShouldBe(expected, because);
            acked.Distinct().Count().ShouldBe(acked.Count, because);

            // Push cancellation: the pending feed holds exactly the rows still unacknowledged (rows without
            // an assigned qts are outside the sequence and never surface here either).
            var ackedUpTo = Math.Max(preAckMaxQts, maxQts);
            var pending = await store.GetForDifferenceAsync(userId, keyId, SecretChatConsts.QtsInitialValue - 1, 0);
            pending.Select(d => d.RandomId)
                .ShouldBe(box.Where(m => m.Qts > ackedUpTo).Select(m => m.RandomId), because);

            // The other device of the same user still has its full queue.
            (await store.GetForDifferenceAsync(userId, otherKeyId, SecretChatConsts.QtsInitialValue - 1, 0))
                .Count.ShouldBe(@case.OtherDeviceCount, because);

            // Draining the rest yields every remaining random_id once; after that nothing is ever returned
            // again — not even for a max_qts far beyond the sequence (the unassigned rows stay excluded).
            (await store.AckAsync(userId, keyId, highest))
                .ShouldBe(box.Where(m => m.Qts > ackedUpTo).Select(m => m.RandomId), because);
            (await store.AckAsync(userId, keyId, highest)).ShouldBeEmpty(because);
            (await store.AckAsync(userId, keyId, int.MaxValue)).ShouldBeEmpty(because);
        }
    }

    /// <summary>
    /// Requirement 13.4 under concurrency: two <c>messages.receivedQueue</c> calls racing with the same
    /// <c>max_qts</c> on the same Authorization_Key must TOGETHER return each <c>random_id</c> exactly once —
    /// never twice, never zero times. The two calls run against the real Mongo-backed store through the real
    /// <see cref="SecretChatAppService"/>, so the exactness comes from the store's per-row conditional
    /// update rather than from any test-side serialization.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task Two_concurrent_receivedQueue_calls_return_every_random_id_exactly_once()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new SecretChatMessageStore(mongo.Database);
        var cases = Sample(ReceivedQueueArbitraries.ConcurrencyCase().Generator, HeavyMongoGeneratedCases);

        for (var i = 0; i < cases.Count; i++)
        {
            var @case = cases[i];
            var fixture = BuildFixture(store, chatId: 20_000 + i, identityBase: 6_000_000L + i * 100);
            var because = $"case #{i} {@case}";

            var randomIds = new List<long>();
            for (var n = 0; n < @case.MessageCount; n++)
            {
                var randomId = 7_000_000L + n;
                await fixture.Service.SendEncryptedAsync(fixture.Admin, fixture.Peer, randomId,
                    new byte[] { 4, (byte)n }, silent: false);
                randomIds.Add(randomId);
            }

            var highest = SecretChatConsts.QtsInitialValue - 1 + @case.MessageCount;
            (await store.GetHighestQtsAsync(fixture.ParticipantUserId, fixture.ParticipantKeyId))
                .ShouldBe(highest, because);

            var maxQts = @case.MaxQtsSelector % (highest + 1);
            var expected = randomIds.Take(Math.Max(0, maxQts - (SecretChatConsts.QtsInitialValue - 1))).ToList();

            var left = fixture.Service.ReceivedQueueAsync(fixture.Participant, maxQts);
            var right = fixture.Service.ReceivedQueueAsync(fixture.Participant, maxQts);
            var results = await Task.WhenAll(left, right);

            var union = results[0].Concat(results[1]).ToList();

            // Exactly once: no random_id is returned by both calls, and together they cover the whole range.
            results[0].Intersect(results[1]).ShouldBeEmpty(because);
            union.Count.ShouldBe(expected.Count, because);
            union.OrderBy(id => id).ShouldBe(expected.OrderBy(id => id), because);

            // Each individual call is itself duplicate-free and ordered by qts.
            foreach (var result in results)
            {
                result.Distinct().Count().ShouldBe(result.Count, because);
                result.ToList().ShouldBe(result.OrderBy(id => id).ToList(), because);
            }

            // The remainder is untouched by the race.
            (await store.GetForDifferenceAsync(fixture.ParticipantUserId, fixture.ParticipantKeyId,
                    SecretChatConsts.QtsInitialValue - 1, 0))
                .Select(d => d.RandomId).ShouldBe(randomIds.Skip(expected.Count), because);
        }
    }

    /// <summary>
    /// Properties 11 and 12 end to end over real persistence: messages sent through the real
    /// <see cref="SecretChatAppService"/> and stored by the real <see cref="SecretChatMessageStore"/> are
    /// acknowledged exactly once by <c>messages.receivedQueue</c>, a repeat call returns an empty vector, an
    /// out-of-range <c>max_qts</c> is rejected with <c>MAX_QTS_INVALID</c> without acknowledging anything,
    /// and the sender's own (never assigned) Authorization_Key rejects <c>max_qts == QtsInitialValue</c>.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task Real_store_receivedQueue_round_trip_and_max_qts_validation()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new SecretChatMessageStore(mongo.Database);
        var cases = Sample(ReceivedQueueArbitraries.ConcurrencyCase().Generator, HeavyMongoGeneratedCases);

        for (var i = 0; i < cases.Count; i++)
        {
            var @case = cases[i];
            var fixture = BuildFixture(store, chatId: 30_000 + i, identityBase: 8_000_000L + i * 100);
            var because = $"case #{i} {@case}";

            var randomIds = new List<long>();
            for (var n = 0; n < @case.MessageCount; n++)
            {
                var randomId = 9_000_000L + n;
                await fixture.Service.SendEncryptedAsync(fixture.Admin, fixture.Peer, randomId,
                    new byte[] { 8, (byte)n }, silent: false);
                randomIds.Add(randomId);
            }

            var highest = SecretChatConsts.QtsInitialValue - 1 + @case.MessageCount;

            // Requirement 13.2: max_qts above the highest assigned value is rejected outright.
            var ex = await Should.ThrowAsync<RpcException>(async () =>
                await fixture.Service.ReceivedQueueAsync(fixture.Participant, highest + 1));
            ex.RpcError.ShouldBe(RpcErrors.RpcErrors400.MaxQtsInvalid, because);

            // Requirement 13.5: the admin's device never received anything, so QtsInitialValue is invalid
            // for it while QtsInitialValue - 1 is accepted.
            (await Should.ThrowAsync<RpcException>(async () =>
                    await fixture.Service.ReceivedQueueAsync(fixture.Admin, SecretChatConsts.QtsInitialValue)))
                .RpcError.ShouldBe(RpcErrors.RpcErrors400.MaxQtsInvalid, because);
            (await fixture.Service.ReceivedQueueAsync(fixture.Admin, SecretChatConsts.QtsInitialValue - 1))
                .ShouldBeEmpty(because);

            // Nothing was acknowledged by the rejected calls.
            (await store.GetForDifferenceAsync(fixture.ParticipantUserId, fixture.ParticipantKeyId,
                    SecretChatConsts.QtsInitialValue - 1, 0))
                .Select(d => d.RandomId).ShouldBe(randomIds, because);

            // Requirements 13.3/13.4: the in-range call returns every random_id once, the repeat is empty.
            var maxQts = @case.MaxQtsSelector % (highest + 1);
            var expected = randomIds.Take(Math.Max(0, maxQts - (SecretChatConsts.QtsInitialValue - 1))).ToList();

            (await fixture.Service.ReceivedQueueAsync(fixture.Participant, maxQts)).ToList()
                .ShouldBe(expected, because);
            (await fixture.Service.ReceivedQueueAsync(fixture.Participant, maxQts)).ShouldBeEmpty(because);

            (await fixture.Service.ReceivedQueueAsync(fixture.Participant, highest)).ToList()
                .ShouldBe(randomIds.Skip(expected.Count).ToList(), because);
            (await fixture.Service.ReceivedQueueAsync(fixture.Participant, highest)).ShouldBeEmpty(because);
        }
    }

    // ---- helpers -----------------------------------------------------------------------------

    /// <summary>
    /// Inserts one message row for the given recipient device exactly the way the production send path does
    /// (insert -&gt; allocate -&gt; set), and returns the assigned qts (0 when the allocation is skipped, i.e.
    /// a row that crashed between insert and allocation).
    /// </summary>
    private static async Task<int> SeedAsync(ISecretChatMessageStore store,
        long chatId,
        long recipientUserId,
        long recipientKeyId,
        long randomId,
        bool assignQts)
    {
        const long senderUserId = 1;
        var document = new EncryptedMessageDocument
        {
            Id = EncryptedMessageDocument.BuildId(chatId, senderUserId, randomId),
            ChatId = chatId,
            UserId = senderUserId,
            PermAuthKeyId = 9,
            RecipientUserId = recipientUserId,
            RecipientPermAuthKeyId = recipientKeyId,
            Data = [1, 2, 3],
            Date = 1000,
            MessageType = SendMessageType.Text,
            RandomId = randomId
        };

        var stored = await store.StoreAsync(document);
        stored.IsNew.ShouldBeTrue();

        if (!assignQts)
        {
            return 0;
        }

        var qts = await store.AllocateQtsAsync(recipientUserId, recipientKeyId);
        await store.SetQtsAsync(document.Id, qts, recipientUserId, recipientKeyId);

        return qts;
    }

    /// <summary>
    /// Builds the real <see cref="SecretChatAppService"/> (with the real
    /// <see cref="SecretChatAccessResolver"/>) over the supplied store for an ACTIVE secret chat whose
    /// identities are derived from <paramref name="identityBase"/>, so every case can own a fresh pair of
    /// Authorization_Keys even when the underlying store is shared.
    /// </summary>
    private static SecretChatFixture BuildFixture(ISecretChatMessageStore store, int chatId, long identityBase)
    {
        var adminId = identityBase + 1;
        var participantId = identityBase + 2;
        var adminKeyId = identityBase + 10;
        var participantKeyId = identityBase + 20;
        var accessHash = identityBase + 999;

        var chat = new FakeEncryptedChatReadModel
        {
            Id = $"encrypted_chat_{chatId}",
            ChatId = chatId,
            AccessHash = accessHash,
            AdminId = adminId,
            ParticipantId = participantId,
            AdminPermAuthKeyId = adminKeyId,
            ParticipantPermAuthKeyId = participantKeyId,
            Ga = SecretChatTestHarness.ValidDhValue(),
            Gb = SecretChatTestHarness.ValidDhValue(),
            KeyFingerprint = 424242,
            ChatState = ChatState.Active,
            Date = 1000,
            RandomId = 42
        };

        var backing = new FakeQueryProcessor();
        backing.Users[adminId] = FakeUser.Create(adminId);
        backing.Users[participantId] = FakeUser.Create(participantId);
        backing.Chats[chatId] = chat;

        // The concurrency fact issues two receivedQueue calls at once; each resolves its caller through the
        // query processor, so the shared fake is guarded (its recording list is not thread-safe).
        var queryProcessor = new SynchronizedQueryProcessor(backing);
        var dispatcher = new RecordingUpdateDispatcher();

        var service = new SecretChatAppService(new RecordingCommandBus(),
            queryProcessor,
            new FakeIdGenerator(),
            new FakeBlockCacheAppService(),
            new SecretChatAccessResolver(queryProcessor),
            dispatcher,
            store,
            new InMemorySecretChatRequestLedger(),
            new InMemoryEncryptedFileStore(),
            SecretChatTestHarness.ChatConverters(),
            SecretChatTestHarness.MessageConverters(),
            SecretChatTestHarness.FileConverters());

        return new SecretChatFixture(service,
            SecretChatTestHarness.Input(adminId, adminKeyId),
            SecretChatTestHarness.Input(participantId, participantKeyId),
            new TInputEncryptedChat { ChatId = chatId, AccessHash = accessHash },
            dispatcher,
            adminId,
            adminKeyId,
            participantId,
            participantKeyId);
    }

    private static IReadOnlyList<T> Sample<T>(Gen<T> generator, int count)
    {
        return Gen.Sample(SampleSize, count, generator).ToList();
    }

    /// <summary>One ACTIVE secret chat plus the two callers' request inputs and the shared transport.</summary>
    private sealed record SecretChatFixture(
        SecretChatAppService Service,
        TestRequestInput Admin,
        TestRequestInput Participant,
        IInputEncryptedChat Peer,
        RecordingUpdateDispatcher Dispatcher,
        long AdminUserId,
        long AdminKeyId,
        long ParticipantUserId,
        long ParticipantKeyId);

    /// <summary>
    /// Serializes access to the shared <see cref="FakeQueryProcessor"/> so the concurrency fact races only
    /// on the production code under test (the Mongo store), never on the fake's bookkeeping.
    /// </summary>
    private sealed class SynchronizedQueryProcessor(IQueryProcessor inner) : IQueryProcessor
    {
        private readonly object _gate = new();

        public Task<TResult> ProcessAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                // The inner fake completes synchronously, so the lock is never held across an await.
                return inner.ProcessAsync(query, cancellationToken);
            }
        }
    }
}

/// <summary>The secret-chat operations a generated receivedQueue case can perform.</summary>
public enum ReceivedQueueOpKind
{
    /// <summary>admin -&gt; participant text message: enters the participant device's qts box.</summary>
    AdminMessage,

    /// <summary>admin -&gt; participant service message: also a message, also carries a qts.</summary>
    AdminServiceMessage,

    /// <summary>participant -&gt; admin message: enters the ADMIN device's box, not the participant's.</summary>
    ParticipantMessage,

    /// <summary>readEncryptedHistory: an update without qts and without random_id.</summary>
    ReadHistory,

    /// <summary>setEncryptedTyping(true): an update without qts and without random_id.</summary>
    TypingOn,

    /// <summary>setEncryptedTyping(false): produces no update at all.</summary>
    TypingOff
}

/// <summary>
/// A queue of secret-chat operations plus a selector for an arbitrary <c>max_qts</c> inside the valid range.
/// </summary>
public sealed record ReceivedQueueAckCase(ReceivedQueueOpKind[] Ops, int MaxQtsSelector)
{
    public override string ToString() =>
        $"Ack(ops=[{string.Join(",", Ops)}], maxQtsSelector={MaxQtsSelector})";
}

/// <summary>
/// A number of delivered messages (possibly zero, i.e. a brand new Authorization_Key) and a strictly
/// positive excess of <c>max_qts</c> over the highest assigned qts.
/// </summary>
public sealed record MaxQtsOverflowCase(int MessageCount, int Excess)
{
    public override string ToString() => $"Overflow(messages={MessageCount}, excess={Excess})";
}

/// <summary>
/// A device box seeded at the store level: messages with an assigned qts, an already-acknowledged prefix,
/// rows whose qts was never assigned, rows belonging to a second device of the same user, and a selector for
/// an arbitrary in-range <c>max_qts</c>.
/// </summary>
public sealed record ReceivedQueueStoreCase(
    int MessageCount,
    int PreAckedPrefix,
    int UnassignedCount,
    int OtherDeviceCount,
    int MaxQtsSelector)
{
    public override string ToString() =>
        $"Store(messages={MessageCount}, preAcked={PreAckedPrefix}, unassigned={UnassignedCount}, " +
        $"otherDevice={OtherDeviceCount}, maxQtsSelector={MaxQtsSelector})";
}

/// <summary>A message count and an in-range <c>max_qts</c> selector for the racing/round-trip facts.</summary>
public sealed record ReceivedQueueConcurrencyCase(int MessageCount, int MaxQtsSelector)
{
    public override string ToString() => $"Concurrency(messages={MessageCount}, maxQtsSelector={MaxQtsSelector})";
}

/// <summary>Generators for the receivedQueue properties.</summary>
public static class ReceivedQueueGen
{
    /// <summary>
    /// Messages dominate the mix so most cases have a non-trivial queue, while every non-message operation
    /// stays reachable (they must never contribute a <c>random_id</c>).
    /// </summary>
    public static Gen<ReceivedQueueOpKind> OpKind =>
        Gen.Frequency(
            Tuple.Create(5, Gen.Constant(ReceivedQueueOpKind.AdminMessage)),
            Tuple.Create(2, Gen.Constant(ReceivedQueueOpKind.AdminServiceMessage)),
            Tuple.Create(2, Gen.Constant(ReceivedQueueOpKind.ParticipantMessage)),
            Tuple.Create(1, Gen.Constant(ReceivedQueueOpKind.ReadHistory)),
            Tuple.Create(1, Gen.Constant(ReceivedQueueOpKind.TypingOn)),
            Tuple.Create(1, Gen.Constant(ReceivedQueueOpKind.TypingOff)));

    /// <summary>An operation queue of 0..14 operations (0 covers the empty-queue case).</summary>
    public static Gen<ReceivedQueueAckCase> AckCase =>
        from length in Gen.Choose(0, 14)
        from ops in Gen.ArrayOf(length, OpKind)
        from selector in Gen.Choose(0, 10_000)
        select new ReceivedQueueAckCase(ops, selector);

    /// <summary>Zero messages is the fresh-Authorization_Key case; the excess is always &gt;= 1.</summary>
    public static Gen<MaxQtsOverflowCase> OverflowCase =>
        from messages in Gen.Choose(0, 10)
        from excess in Gen.Choose(1, 5_000)
        select new MaxQtsOverflowCase(messages, excess);

    public static Gen<ReceivedQueueStoreCase> StoreCase =>
        from messages in Gen.Choose(0, 8)
        from preAcked in Gen.Choose(0, 8)
        from unassigned in Gen.Choose(0, 3)
        from otherDevice in Gen.Choose(0, 3)
        from selector in Gen.Choose(0, 10_000)
        select new ReceivedQueueStoreCase(messages, preAcked, unassigned, otherDevice, selector);

    public static Gen<ReceivedQueueConcurrencyCase> ConcurrencyCase =>
        from messages in Gen.Choose(0, 10)
        from selector in Gen.Choose(0, 10_000)
        select new ReceivedQueueConcurrencyCase(messages, selector);
}

/// <summary>
/// FsCheck arbitrary registration surface for the receivedQueue properties. The Mongo-backed facts cannot
/// carry <c>[Property]</c> (they need a real MongoDB via <c>[RequiresMongoDbFact]</c>), so they sample these
/// arbitraries' generators directly.
/// </summary>
public static class ReceivedQueueArbitraries
{
    public static Arbitrary<ReceivedQueueAckCase> AckCase() => Arb.From(ReceivedQueueGen.AckCase);

    public static Arbitrary<MaxQtsOverflowCase> OverflowCase() => Arb.From(ReceivedQueueGen.OverflowCase);

    public static Arbitrary<ReceivedQueueStoreCase> StoreCase() => Arb.From(ReceivedQueueGen.StoreCase);

    public static Arbitrary<ReceivedQueueConcurrencyCase> ConcurrencyCase() =>
        Arb.From(ReceivedQueueGen.ConcurrencyCase);
}
