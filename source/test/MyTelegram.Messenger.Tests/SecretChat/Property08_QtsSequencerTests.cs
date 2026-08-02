using FsCheck;
using MongoDB.Driver;
using MyTelegram;
using MyTelegram.Messenger.Services.SecretChat;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.SecretChat;

/// <summary>
/// Feature: secret-chats, Property 8: qts monotonicity, contiguity and uniqueness per Authorization_Key —
/// and Property 9: qts sequence independence between Authorization_Keys.
///
/// <para><b>Property 8.</b> For any Authorization_Key and any — including concurrent — sequence of qts
/// allocations: the assigned values are strictly increasing, each is exactly 1 greater than the previously
/// assigned value (no skipped value), and no value is assigned twice. The first value assigned to a
/// previously unused Authorization_Key equals a fixed positive initial value that is identical for every
/// key (<see cref="SecretChatConsts.QtsInitialValue"/>), and a key that has already been assigned values
/// continues its sequence after a "reconnect" instead of restarting from the initial value.
/// <b>Validates: Requirements 12.1, 12.2, 12.3, 12.5, 12.6.</b></para>
///
/// <para><b>Property 9.</b> For any update delivered to more than one Authorization_Key, every recipient
/// key draws its qts from its own sequence: the value a key receives depends only on how many values that
/// key was assigned before, never on the traffic of any other key.
/// <b>Validates: Requirement 12.4.</b></para>
///
/// <para><b>How this is tested.</b> The qts sequencer is a persistence-level concern (an atomic
/// <c>$inc</c> upsert on a per-key counter document), so — unlike the other secret-chat properties, which
/// run against in-memory fakes — these tests drive the REAL production
/// <see cref="SecretChatMessageStore"/> against a REAL <c>mongod</c> instance started by
/// <see cref="EmbeddedMongoServer"/>. Nothing about the sequence is simulated: contiguity, atomicity and
/// persistence are all decided by MongoDB. When no <c>mongod</c> binary is available the tests skip
/// cleanly via <see cref="RequiresMongoDbFactAttribute"/>.</para>
///
/// <para>Because <c>[Property]</c> (FsCheck) and <c>[RequiresMongoDbFact]</c> cannot be combined on one
/// method, the generated cases are produced with the FsCheck generators in <see cref="QtsGen"/> /
/// <see cref="QtsArbitraries"/> and sampled with <c>Gen.Sample</c> INSIDE each fact, which then loops over
/// at least 100 generated cases (40 for the two heavier concurrency/interleaving facts, each of which
/// performs tens of allocations per case). What is generated: the number of sequential allocations and the
/// key salt (Property 8.1), the pre-/post-reconnect allocation counts and the number of reconnects
/// (Property 8.2), the sizes of two concurrent allocation bursts (Property 8.3), an arbitrary interleaving
/// of allocations across several distinct keys (Property 9.1) and, per update, an arbitrary non-empty
/// recipient subset expressed as a bitmask (Property 9.2). Every case gets its own never-used
/// (userId, permAuthKeyId) pair, so each case starts from a genuinely fresh key.</para>
///
/// <para>The expected values are computed independently of the production code from the property statement
/// alone — "the j-th value assigned to a key is <c>QtsInitialValue + j - 1</c>" — and never read back from
/// the store. Uniqueness is asserted separately from contiguity (a distinct-count check), the concurrency
/// bursts are asserted as a SET equality against <c>{QtsInitialValue .. QtsInitialValue + N - 1}</c> so
/// that a lost update, a duplicate or a gap all fail, and reconnects are modelled by constructing a brand
/// new <see cref="SecretChatMessageStore"/> (and, once per case, a brand new <see cref="MongoClient"/>)
/// over the same database, so no part of the sequence can survive in process memory.</para>
///
/// <para>One fact additionally drives the real <see cref="SecretChatAppService"/> over the real Mongo store
/// to pin the property to the update actually delivered: only <c>updateNewEncryptedMessage</c> carries a
/// qts, its qts is the one the sequencer assigned, and a duplicate <c>random_id</c> burns no value.</para>
/// </summary>
public class Property08_QtsSequencerTests
{
    /// <summary>
    /// Allocates a qts AND commits it, mirroring the production send path
    /// (insert row -> allocate -> set qts -> push). The row insert is not optional: <c>SetQtsAsync</c> is
    /// conditional on <c>Qts == 0</c>, so committing against an id that has no row reports "already
    /// sequenced" and releases the allocation instead of publishing it.
    /// <para><c>GetHighestQtsAsync</c> deliberately reports neither the raw allocator nor the bare
    /// DELIVERED watermark, but <c>min(delivered, lowest live in-flight allocation - 1)</c>. A bare
    /// allocation therefore does not move it — it actively holds it down until the row is written —
    /// otherwise updates.getState could advertise a qts whose message row is not yet visible to
    /// updates.getDifference and the client would skip past it permanently.</para>
    /// </summary>
    private static async Task<int> AllocateAndCommitAsync(ISecretChatMessageStore store,
        long userId,
        long permAuthKeyId)
    {
        var qts = await store.AllocateQtsAsync(userId, permAuthKeyId);
        var id = EncryptedMessageDocument.BuildId(userId, permAuthKeyId, qts);

        await store.StoreAsync(new EncryptedMessageDocument
        {
            Id = id,
            ChatId = permAuthKeyId,
            UserId = userId,
            RecipientUserId = userId,
            RecipientPermAuthKeyId = permAuthKeyId,
            Data = [],
            RandomId = qts,
            MessageType = SendMessageType.Text,
            CreatedAt = DateTime.UtcNow
        });

        await store.SetQtsAsync(id, qts, userId, permAuthKeyId);

        return qts;
    }

    /// <summary>Generated cases per fact — the property runs a minimum of 100 cases where feasible.</summary>
    private const int GeneratedCases = 100;

    /// <summary>Cases for the facts whose single case already performs tens of allocations.</summary>
    private const int HeavyGeneratedCases = 40;

    /// <summary>FsCheck size parameter for <c>Gen.Sample</c>; every generator here is size-independent.</summary>
    private const int SampleSize = 50;

    // ==========================================================================================
    // Property 8 — per-key monotonicity, contiguity, uniqueness, fixed initial value, no reset.
    // ==========================================================================================

    /// <summary>
    /// Requirements 12.1, 12.2, 12.3: for every generated allocation count on a fresh Authorization_Key the
    /// assigned values start at the fixed positive initial value, grow by exactly one, never repeat, and
    /// <c>GetHighestQtsAsync</c> reports <c>QtsInitialValue - 1</c> before the first allocation and the last
    /// assigned value afterwards.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task Every_fresh_key_starts_at_the_same_initial_value_and_grows_by_exactly_one()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new SecretChatMessageStore(mongo.Database);

        var cases = Sample(QtsArbitraries.SequenceCase().Generator, GeneratedCases);
        var firstAssignedValues = new List<int>();

        for (var i = 0; i < cases.Count; i++)
        {
            var @case = cases[i];

            // Every case gets its own never-used Authorization_Key (the case index makes it unique).
            var userId = 900_000 + i;
            var permAuthKeyId = 700_000 + @case.KeySalt;
            var because = $"case #{i} {@case}";

            // Requirement 12.3 (read side): a key that was never assigned a qts sits one below the initial
            // value, so that messages.receivedQueue with max_qts == QtsInitialValue - 1 is still valid.
            (await store.GetHighestQtsAsync(userId, permAuthKeyId))
                .ShouldBe(SecretChatConsts.QtsInitialValue - 1, because);

            var assigned = new List<int>();
            for (var n = 0; n < @case.AllocationCount; n++)
            {
                assigned.Add(await AllocateAndCommitAsync(store, userId, permAuthKeyId));
            }

            AssertContiguousSequence(assigned, SecretChatConsts.QtsInitialValue, because);

            (await store.GetHighestQtsAsync(userId, permAuthKeyId)).ShouldBe(assigned[^1], because);

            firstAssignedValues.Add(assigned[0]);
        }

        // Requirement 12.3: the initial value is a fixed POSITIVE integer, identical for EVERY key —
        // asserted across all the distinct fresh keys the generated cases produced.
        SecretChatConsts.QtsInitialValue.ShouldBeGreaterThan(0);
        firstAssignedValues.Count.ShouldBe(cases.Count);
        firstAssignedValues.Distinct().ShouldHaveSingleItem().ShouldBe(SecretChatConsts.QtsInitialValue);
    }

    /// <summary>
    /// Requirement 12.5: a key that has already been assigned qts values continues its sequence after a
    /// reconnect and is never reset to the initial value. A "reconnect" is a brand new
    /// <see cref="SecretChatMessageStore"/> — and for the final session a brand new
    /// <see cref="MongoClient"/> — over the same database, so the sequence can only come from storage.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task A_previously_used_key_continues_its_sequence_across_reconnects_and_never_resets()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var databaseName = mongo.Database.DatabaseNamespace.DatabaseName;

        var cases = Sample(QtsArbitraries.ReconnectCase().Generator, GeneratedCases);

        for (var i = 0; i < cases.Count; i++)
        {
            var @case = cases[i];
            var userId = 1_100_000 + i;
            var permAuthKeyId = 500 + i % 7;
            var because = $"case #{i} {@case}";

            var assigned = new List<int>();
            var firstValueAfterEachReconnect = new List<int>();

            for (var session = 0; session <= @case.Reconnects; session++)
            {
                // Session 0 is the original connection; every later session is a reconnect served by a
                // freshly constructed store (the last one even by a freshly constructed client).
                var sessionStore = session == @case.Reconnects
                    ? new SecretChatMessageStore(new MongoClient(mongo.Client.Settings).GetDatabase(databaseName))
                    : new SecretChatMessageStore(mongo.Database);

                // On reconnect the key's high-water mark is its last assigned value, not the initial value.
                var highestOnConnect = await sessionStore.GetHighestQtsAsync(userId, permAuthKeyId);
                highestOnConnect.ShouldBe(assigned.Count == 0
                        ? SecretChatConsts.QtsInitialValue - 1
                        : assigned[^1],
                    because);

                var allocations = session == 0 ? @case.BeforeReconnect : @case.AfterReconnect;
                for (var n = 0; n < allocations; n++)
                {
                    var qts = await AllocateAndCommitAsync(sessionStore, userId, permAuthKeyId);
                    if (n == 0 && session > 0)
                    {
                        firstValueAfterEachReconnect.Add(qts);
                    }

                    assigned.Add(qts);
                }
            }

            // Requirements 12.1/12.2 hold ACROSS the reconnects: one uninterrupted contiguous sequence.
            AssertContiguousSequence(assigned, SecretChatConsts.QtsInitialValue, because);

            // Requirement 12.5, stated directly: no post-reconnect allocation ever falls back to the
            // initial value (the sequence never restarts).
            firstValueAfterEachReconnect.Count.ShouldBe(@case.Reconnects, because);
            foreach (var value in firstValueAfterEachReconnect)
            {
                value.ShouldBeGreaterThan(SecretChatConsts.QtsInitialValue, because);
            }
        }
    }

    /// <summary>
    /// Requirement 12.6: qts values assigned concurrently for one Authorization_Key stay distinct,
    /// contiguous and gap-free. Each generated burst fires N allocations at once through
    /// <see cref="Task.WhenAll(System.Collections.Generic.IEnumerable{Task})"/> and the resulting multiset
    /// must be exactly <c>{QtsInitialValue .. QtsInitialValue + N - 1}</c>; a second concurrent burst must
    /// continue from where the first one ended.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task Concurrent_allocations_for_one_key_are_distinct_contiguous_and_gap_free()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new SecretChatMessageStore(mongo.Database);

        var cases = Sample(QtsArbitraries.BurstCase().Generator, HeavyGeneratedCases);

        for (var i = 0; i < cases.Count; i++)
        {
            var @case = cases[i];
            var userId = 1_200_000 + i;
            const long permAuthKeyId = 42;
            var because = $"case #{i} {@case}";

            var firstWave = await Task.WhenAll(Enumerable
                .Range(0, @case.FirstBurst)
                .Select(_ => AllocateAndCommitAsync(store, userId, permAuthKeyId)));

            // Exactly one value per concurrent allocation, no duplicate, no gap, none lost.
            AssertContiguousSet(firstWave, SecretChatConsts.QtsInitialValue, because);

            if (@case.SecondBurst > 0)
            {
                var secondWave = await Task.WhenAll(Enumerable
                    .Range(0, @case.SecondBurst)
                    .Select(_ => AllocateAndCommitAsync(store, userId, permAuthKeyId)));

                // The second burst continues the SAME sequence: it starts one past the first burst's top.
                AssertContiguousSet(secondWave, SecretChatConsts.QtsInitialValue + @case.FirstBurst, because);
                firstWave.Intersect(secondWave).ShouldBeEmpty(because);
            }

            var total = @case.FirstBurst + @case.SecondBurst;
            (await store.GetHighestQtsAsync(userId, permAuthKeyId))
                .ShouldBe(SecretChatConsts.QtsInitialValue + total - 1, because);
        }
    }

    /// <summary>
    /// Requirements 12.1, 12.2, 12.3 at the delivered-update level: driving the real
    /// <see cref="SecretChatAppService"/> over the real Mongo-backed store, every
    /// <c>updateNewEncryptedMessage</c> pushed to the recipient device carries the next value of that
    /// device's sequence, and a resend of an already-used <c>random_id</c> allocates nothing (which would
    /// otherwise punch a permanent gap into the recipient's sequence).
    /// </summary>
    [RequiresMongoDbFact]
    public async Task Delivered_encrypted_messages_carry_the_recipient_device_qts_and_duplicates_burn_none()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new SecretChatMessageStore(mongo.Database);

        var queryProcessor = new FakeQueryProcessor();
        queryProcessor.Users[SecretChatTestHarness.AdminId] = FakeUser.Create(SecretChatTestHarness.AdminId);
        queryProcessor.Users[SecretChatTestHarness.ParticipantId] =
            FakeUser.Create(SecretChatTestHarness.ParticipantId);
        queryProcessor.Chats[SecretChatTestHarness.ChatId] = SecretChatTestHarness.Chat();

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

        var adminInput = SecretChatTestHarness.Input(SecretChatTestHarness.AdminId,
            SecretChatTestHarness.AdminPermAuthKeyId);
        var participantInput = SecretChatTestHarness.Input(SecretChatTestHarness.ParticipantId,
            SecretChatTestHarness.ParticipantPermAuthKeyId);
        var peer = SecretChatTestHarness.InputChat();

        const int messageCount = 12;
        for (var i = 0; i < messageCount; i++)
        {
            await service.SendEncryptedAsync(adminInput, peer, randomId: 1000 + i, SecretChatTestHarness.Payload(1, 2, (byte)i),
                silent: false);
        }

        dispatcher.Dispatched.Count.ShouldBe(messageCount);

        var deliveredQts = new List<int>();
        for (var i = 0; i < messageCount; i++)
        {
            var dispatched = dispatcher.Dispatched[i];

            // Only the recipient's bound device is targeted, and only updateNewEncryptedMessage carries qts.
            dispatched.UserId.ShouldBe(SecretChatTestHarness.ParticipantId);
            dispatched.OnlySendToThisAuthKeyId.ShouldBe(SecretChatTestHarness.ParticipantPermAuthKeyId);

            var update = dispatched.Update.ShouldBeOfType<TUpdateNewEncryptedMessage>();
            dispatched.Qts.ShouldBe(update.Qts);
            deliveredQts.Add(update.Qts);
        }

        AssertContiguousSequence(deliveredQts, SecretChatConsts.QtsInitialValue, "admin -> participant");
        (await store.GetHighestQtsAsync(SecretChatTestHarness.ParticipantId,
            SecretChatTestHarness.ParticipantPermAuthKeyId)).ShouldBe(deliveredQts[^1]);

        // A duplicate random_id is deduplicated: no new update, and the sequence is left exactly where it
        // was (Requirement 12.1 — a burnt value would show up as a permanent gap for the recipient).
        await service.SendEncryptedAsync(adminInput, peer, randomId: 1003, SecretChatTestHarness.Payload(9, 9), silent: false);
        dispatcher.Dispatched.Count.ShouldBe(messageCount);
        (await store.GetHighestQtsAsync(SecretChatTestHarness.ParticipantId,
            SecretChatTestHarness.ParticipantPermAuthKeyId)).ShouldBe(deliveredQts[^1]);

        // Requirement 12.4 in the smallest real setting: the reply travels to the ADMIN's device, whose own
        // sequence is untouched by the 12 updates just delivered to the participant, so it starts over at
        // the initial value.
        await service.SendEncryptedAsync(participantInput, peer, randomId: 7777, SecretChatTestHarness.Payload(5), silent: false);

        dispatcher.Dispatched.Count.ShouldBe(messageCount + 1);
        var reply = dispatcher.Dispatched[^1];
        reply.UserId.ShouldBe(SecretChatTestHarness.AdminId);
        reply.OnlySendToThisAuthKeyId.ShouldBe(SecretChatTestHarness.AdminPermAuthKeyId);
        reply.Update.ShouldBeOfType<TUpdateNewEncryptedMessage>().Qts.ShouldBe(SecretChatConsts.QtsInitialValue);

        // ... and the participant's sequence is unaffected by the admin's allocation.
        (await store.GetHighestQtsAsync(SecretChatTestHarness.ParticipantId,
            SecretChatTestHarness.ParticipantPermAuthKeyId)).ShouldBe(deliveredQts[^1]);
    }

    // ==========================================================================================
    // Property 9 — sequence independence between Authorization_Keys (Requirement 12.4).
    // ==========================================================================================

    /// <summary>
    /// Requirement 12.4: for an arbitrary interleaving of allocations across several distinct
    /// Authorization_Keys, each key's own sequence is contiguous from the initial value and completely
    /// unaffected by the interleaved traffic of the other keys. The key set deliberately contains two
    /// devices of the SAME user and two users sharing the SAME <c>perm_auth_key_id</c> value, so that
    /// neither half of the key alone can be collapsing the sequences.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task Interleaved_allocations_across_keys_keep_every_key_sequence_independent()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new SecretChatMessageStore(mongo.Database);

        var cases = Sample(QtsArbitraries.InterleavingCase().Generator, HeavyGeneratedCases);

        for (var i = 0; i < cases.Count; i++)
        {
            var @case = cases[i];
            var keys = BuildKeySet(1_300_000 + i * 10);
            var because = $"case #{i} {@case}";

            var perKey = new List<int>[keys.Length];
            for (var k = 0; k < keys.Length; k++)
            {
                perKey[k] = [];
            }

            foreach (var pick in @case.Picks)
            {
                var (userId, permAuthKeyId) = keys[pick];
                perKey[pick].Add(await AllocateAndCommitAsync(store, userId, permAuthKeyId));
            }

            for (var k = 0; k < keys.Length; k++)
            {
                var (userId, permAuthKeyId) = keys[k];
                var keyBecause = $"{because}, key #{k} ({userId}, {permAuthKeyId})";

                if (perKey[k].Count == 0)
                {
                    // A key nobody delivered to is still untouched, whatever the other keys did.
                    (await store.GetHighestQtsAsync(userId, permAuthKeyId))
                        .ShouldBe(SecretChatConsts.QtsInitialValue - 1, keyBecause);

                    continue;
                }

                // Independence: the j-th value this key received is QtsInitialValue + j - 1, i.e. it depends
                // only on this key's own history and not at all on the interleaving.
                AssertContiguousSequence(perKey[k], SecretChatConsts.QtsInitialValue, keyBecause);
                (await store.GetHighestQtsAsync(userId, permAuthKeyId)).ShouldBe(perKey[k][^1], keyBecause);
            }
        }

        // The per-key counter identity must not be ambiguous either: (1, 23) and (12, 3) are two different
        // Authorization_Keys and may not share a sequence just because their ids concatenate alike.
        (await AllocateAndCommitAsync(store, 1, 23)).ShouldBe(SecretChatConsts.QtsInitialValue);
        (await AllocateAndCommitAsync(store, 12, 3)).ShouldBe(SecretChatConsts.QtsInitialValue);
        (await AllocateAndCommitAsync(store, 1, 23)).ShouldBe(SecretChatConsts.QtsInitialValue + 1);
        (await store.GetHighestQtsAsync(12, 3)).ShouldBe(SecretChatConsts.QtsInitialValue);
    }

    // ==========================================================================================
    // The in-flight allocation set: an allocated-but-unwritten qts must hold the advertised
    // watermark down, or a concurrent send publishes over it and the message is lost for good.
    // ==========================================================================================

    /// <summary>
    /// The core invariant: every qts in <c>(QtsInitialValue - 1, GetHighestQtsAsync()]</c> is already
    /// written onto its row.
    /// <para>Send A allocates and stalls before writing its row; send B — to the SAME recipient device,
    /// typically a different secret chat on that user's primary device — allocates, writes and publishes.
    /// The delivered watermark is a <c>$max</c>, so on its own it would now read B's qts and a
    /// getDifference landing here would hand the client a cursor past A. A's row later becomes visible
    /// below that cursor and <c>GetForDifferenceAsync</c> (filtering <c>Qts &gt; sinceQts</c>) can never
    /// return it again.</para>
    /// <para>Before the in-flight set this asserted 2 and lost the message; the watermark must stay at
    /// <c>QtsInitialValue - 1</c> until A commits.</para>
    /// </summary>
    [RequiresMongoDbFact]
    public async Task An_uncommitted_allocation_holds_the_watermark_below_a_later_committed_one()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new SecretChatMessageStore(mongo.Database);

        const long userId = 951_001;
        const long permAuthKeyId = 751_001;

        // Send A: allocates, then stalls (crash / slow round-trip) before its row carries the qts.
        var qtsA = await store.AllocateQtsAsync(userId, permAuthKeyId);
        qtsA.ShouldBe(SecretChatConsts.QtsInitialValue);
        var idA = EncryptedMessageDocument.BuildId(userId, permAuthKeyId, qtsA);
        await store.StoreAsync(NewRow(idA, userId, permAuthKeyId, qtsA));

        // Send B: allocates, writes and publishes while A is still in flight.
        var qtsB = await AllocateAndCommitAsync(store, userId, permAuthKeyId);
        qtsB.ShouldBe(SecretChatConsts.QtsInitialValue + 1);

        // --- inside the window: B is delivered, but advertising its qts would strand A ---
        (await store.GetHighestQtsAsync(userId, permAuthKeyId))
            .ShouldBe(SecretChatConsts.QtsInitialValue - 1, "an unwritten allocation must hold the watermark");

        var advertised = await store.GetHighestQtsAsync(userId, permAuthKeyId);

        // The page must be bounded by the same watermark, or a truncated page's cursor jumps the hole.
        (await store.GetForDifferenceAsync(userId, permAuthKeyId, SecretChatConsts.QtsInitialValue - 1,
                limit: 0, maxQts: advertised))
            .ShouldBeEmpty("no row may be handed out above the advertised watermark");

        // --- A finally commits: both messages become visible and reachable from the old cursor ---
        (await store.SetQtsAsync(idA, qtsA, userId, permAuthKeyId)).ShouldBeTrue();

        (await store.GetHighestQtsAsync(userId, permAuthKeyId)).ShouldBe(qtsB);

        var recovered = await store.GetForDifferenceAsync(userId, permAuthKeyId, advertised, limit: 0,
            maxQts: await store.GetHighestQtsAsync(userId, permAuthKeyId));

        recovered.Select(d => d.Qts).ShouldBe([qtsA, qtsB],
            "the stalled message must still be reachable from the cursor advertised while it was in flight");
    }

    /// <summary>
    /// A sender that dies between allocate and set must not wedge the device forever: the entry expires
    /// after <c>InflightStaleAfter</c>, the value is BURNT (a permanent hole in the numbering, which is
    /// safe because every consumer filters qts as a range), and the sequence carries on.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task An_abandoned_allocation_expires_and_is_burnt_rather_than_wedging_the_watermark()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new SecretChatMessageStore(mongo.Database);

        const long userId = 952_002;
        const long permAuthKeyId = 752_002;

        var abandoned = await store.AllocateQtsAsync(userId, permAuthKeyId);
        abandoned.ShouldBe(SecretChatConsts.QtsInitialValue);

        // Still inside the staleness window: held down, exactly as the previous fact requires.
        (await store.GetHighestQtsAsync(userId, permAuthKeyId))
            .ShouldBe(SecretChatConsts.QtsInitialValue - 1);

        // Same data, a reader that considers every allocation stale: unwedged, back to DELIVERED.
        var aged = new SecretChatMessageStore(mongo.Database) { InflightStaleAfter = TimeSpan.Zero };

        (await aged.GetHighestQtsAsync(userId, permAuthKeyId))
            .ShouldBe(SecretChatConsts.QtsInitialValue - 1, "delivered is still 0, but nothing is wedged");

        // The abandoned value is never reused, and normal service resumes on top of the hole.
        var next = await AllocateAndCommitAsync(aged, userId, permAuthKeyId);
        next.ShouldBe(SecretChatConsts.QtsInitialValue + 1, "the abandoned value is burnt, not recycled");
        (await aged.GetHighestQtsAsync(userId, permAuthKeyId)).ShouldBe(next);
    }

    /// <summary>
    /// Explicitly releasing an allocation frees the watermark immediately, without waiting out the
    /// staleness window. This is the path <c>AssignQtsAndDispatchAsync</c> takes when the send throws
    /// after allocating.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task Abandoning_an_allocation_releases_the_watermark_immediately()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new SecretChatMessageStore(mongo.Database);

        const long userId = 953_003;
        const long permAuthKeyId = 753_003;

        var committed = await AllocateAndCommitAsync(store, userId, permAuthKeyId);
        var doomed = await store.AllocateQtsAsync(userId, permAuthKeyId);

        (await store.GetHighestQtsAsync(userId, permAuthKeyId))
            .ShouldBe(doomed - 1, "the live allocation clamps the watermark below itself");

        await store.AbandonQtsAsync(doomed, userId, permAuthKeyId);

        (await store.GetHighestQtsAsync(userId, permAuthKeyId))
            .ShouldBe(committed, "releasing the allocation restores the delivered watermark at once");
    }

    /// <summary>
    /// <c>SetQtsAsync</c> is conditional on <c>Qts == 0</c>: two requests racing to finish the same
    /// interrupted send must not both publish, or the recipient receives the message twice.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task Only_the_first_writer_sequences_a_row_and_the_loser_reports_it()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new SecretChatMessageStore(mongo.Database);

        const long userId = 954_004;
        const long permAuthKeyId = 754_004;

        var first = await store.AllocateQtsAsync(userId, permAuthKeyId);
        var second = await store.AllocateQtsAsync(userId, permAuthKeyId);

        var id = EncryptedMessageDocument.BuildId(userId, permAuthKeyId, first);
        await store.StoreAsync(NewRow(id, userId, permAuthKeyId, first));

        (await store.SetQtsAsync(id, first, userId, permAuthKeyId)).ShouldBeTrue();
        (await store.SetQtsAsync(id, second, userId, permAuthKeyId))
            .ShouldBeFalse("the row is already sequenced, so the caller must not push again");

        // The loser's allocation is released, so it does not hold the watermark down for a minute.
        (await store.GetHighestQtsAsync(userId, permAuthKeyId)).ShouldBe(first);

        // The row keeps the FIRST writer's qts; the second value is burnt.
        var rows = await store.GetForDifferenceAsync(userId, permAuthKeyId,
            SecretChatConsts.QtsInitialValue - 1, limit: 0);
        rows.Select(d => d.Qts).ShouldBe([first]);
    }

    /// <summary>
    /// The invariant as a property, over random interleavings of allocate / commit-an-outstanding-one:
    /// after EVERY step, every qts up to the advertised watermark is already written onto a row.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task The_watermark_never_advertises_a_qts_whose_row_is_not_yet_written()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new SecretChatMessageStore(mongo.Database);

        var schedules = Sample(QtsGen.SequenceCase, HeavyGeneratedCases);

        for (var i = 0; i < schedules.Count; i++)
        {
            var userId = 960_000 + i;
            var permAuthKeyId = 760_000 + schedules[i].KeySalt;
            var because = $"case #{i} {schedules[i]}";

            // Outstanding allocations, committed out of order so a later value can land before an earlier.
            var outstanding = new List<int>();
            var steps = Math.Max(2, schedules[i].AllocationCount);

            for (var step = 0; step < steps * 2; step++)
            {
                // Alternate: allocate on even steps, commit the NEWEST outstanding on odd ones (LIFO is
                // what actually reorders the commits and reproduces the race).
                if (step % 2 == 0 || outstanding.Count == 0)
                {
                    var qts = await store.AllocateQtsAsync(userId, permAuthKeyId);
                    await store.StoreAsync(NewRow(EncryptedMessageDocument.BuildId(userId, permAuthKeyId, qts),
                        userId, permAuthKeyId, qts));
                    outstanding.Add(qts);
                }
                else
                {
                    var qts = outstanding[^1];
                    outstanding.RemoveAt(outstanding.Count - 1);
                    await store.SetQtsAsync(EncryptedMessageDocument.BuildId(userId, permAuthKeyId, qts), qts,
                        userId, permAuthKeyId);
                }

                var watermark = await store.GetHighestQtsAsync(userId, permAuthKeyId);
                var visible = (await store.GetForDifferenceAsync(userId, permAuthKeyId,
                        SecretChatConsts.QtsInitialValue - 1, limit: 0, maxQts: watermark))
                    .Select(d => d.Qts)
                    .ToHashSet();

                for (var q = SecretChatConsts.QtsInitialValue; q <= watermark; q++)
                {
                    visible.ShouldContain(q,
                        $"{because}, step {step}: watermark {watermark} advertises qts {q} with no visible row");
                }
            }
        }
    }

    /// <summary>Minimal stored row for sequencer facts; the payload is irrelevant to qts assignment.</summary>
    private static EncryptedMessageDocument NewRow(string id, long userId, long permAuthKeyId, long randomId) =>
        new()
        {
            Id = id,
            ChatId = permAuthKeyId,
            UserId = userId,
            RecipientUserId = userId,
            RecipientPermAuthKeyId = permAuthKeyId,
            Data = [],
            RandomId = randomId,
            MessageType = SendMessageType.Text,
            CreatedAt = DateTime.UtcNow
        };

    /// <summary>
    /// Requirement 12.4, stated as the fan-out itself: each generated update is delivered to an arbitrary
    /// non-empty subset of the Authorization_Keys, and every recipient of that one update draws its own
    /// value from its own sequence. The final round is delivered to every key CONCURRENTLY to show the
    /// independence also holds when the fan-out is parallel.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task An_update_fanned_out_to_several_keys_draws_one_qts_from_each_key_own_sequence()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new SecretChatMessageStore(mongo.Database);

        var cases = Sample(QtsArbitraries.FanOutCase().Generator, GeneratedCases);

        for (var i = 0; i < cases.Count; i++)
        {
            var @case = cases[i];
            var keys = BuildKeySet(1_400_000 + i * 10);
            var because = $"case #{i} {@case}";

            // Expected next value per key, derived from the property statement alone.
            var expectedNext = new int[keys.Length];
            Array.Fill(expectedNext, SecretChatConsts.QtsInitialValue);

            foreach (var mask in @case.RecipientMasks)
            {
                var recipients = Recipients(mask, keys.Length);
                recipients.ShouldNotBeEmpty(because);

                foreach (var k in recipients)
                {
                    var (userId, permAuthKeyId) = keys[k];
                    var qts = await AllocateAndCommitAsync(store, userId, permAuthKeyId);

                    // This key's own next value — independent of how many other keys received this update.
                    qts.ShouldBe(expectedNext[k], $"{because}, key #{k}");
                    expectedNext[k]++;
                }
            }

            // One last update fanned out to EVERY key at once.
            var concurrent = await Task.WhenAll(Enumerable.Range(0, keys.Length)
                .Select(k => AllocateAndCommitAsync(store, keys[k].UserId, keys[k].PermAuthKeyId)));

            for (var k = 0; k < keys.Length; k++)
            {
                concurrent[k].ShouldBe(expectedNext[k], $"{because}, concurrent fan-out, key #{k}");
                expectedNext[k]++;
            }

            for (var k = 0; k < keys.Length; k++)
            {
                var (userId, permAuthKeyId) = keys[k];
                (await store.GetHighestQtsAsync(userId, permAuthKeyId))
                    .ShouldBe(expectedNext[k] - 1, $"{because}, key #{k}");
            }
        }
    }

    // ---- helpers -----------------------------------------------------------------------------

    /// <summary>
    /// Four distinct Authorization_Keys built from one base id: two devices of the same user, a second user
    /// reusing the FIRST user's <c>perm_auth_key_id</c> value, and an unrelated key.
    /// </summary>
    private static (long UserId, long PermAuthKeyId)[] BuildKeySet(long baseId)
    {
        return
        [
            (baseId + 1, 10),
            (baseId + 1, 20),
            (baseId + 2, 10),
            (baseId + 3, 30)
        ];
    }

    /// <summary>Expands a recipient bitmask into the key indices the update is delivered to.</summary>
    private static IReadOnlyList<int> Recipients(int mask, int keyCount)
    {
        var recipients = new List<int>(keyCount);
        for (var k = 0; k < keyCount; k++)
        {
            if ((mask & (1 << k)) != 0)
            {
                recipients.Add(k);
            }
        }

        return recipients;
    }

    /// <summary>
    /// Requirements 12.1/12.2/12.3 for one key's assignment history: starts at
    /// <paramref name="expectedFirst"/>, each value exactly one greater than the previous one (strictly
    /// increasing, no gap), and no value assigned twice.
    /// </summary>
    private static void AssertContiguousSequence(IReadOnlyList<int> values, int expectedFirst, string because)
    {
        values.Count.ShouldBeGreaterThan(0, because);
        values[0].ShouldBe(expectedFirst, because);

        for (var i = 1; i < values.Count; i++)
        {
            values[i].ShouldBe(values[i - 1] + 1, $"{because}, at index {i}");
        }

        values.Distinct().Count().ShouldBe(values.Count, because);
    }

    /// <summary>
    /// Requirement 12.6 for a set of concurrently assigned values: exactly
    /// <c>{expectedFirst .. expectedFirst + count - 1}</c>, i.e. no duplicate, no gap and nothing lost —
    /// asserted on the SET because concurrent completion order carries no meaning.
    /// </summary>
    private static void AssertContiguousSet(IReadOnlyCollection<int> values, int expectedFirst, string because)
    {
        values.Distinct().Count().ShouldBe(values.Count, because);
        values.OrderBy(v => v).ShouldBe(Enumerable.Range(expectedFirst, values.Count), because);
    }

    private static IReadOnlyList<T> Sample<T>(Gen<T> generator, int count)
    {
        return Gen.Sample(SampleSize, count, generator).ToList();
    }
}

/// <summary>Sequential allocations on one previously unused Authorization_Key.</summary>
public sealed record QtsSequenceCase(int AllocationCount, int KeySalt)
{
    public override string ToString() => $"Sequence(allocations={AllocationCount}, keySalt={KeySalt})";
}

/// <summary>Allocations before and after one or more "reconnects" of the same Authorization_Key.</summary>
public sealed record QtsReconnectCase(int BeforeReconnect, int AfterReconnect, int Reconnects)
{
    public override string ToString() =>
        $"Reconnect(before={BeforeReconnect}, after={AfterReconnect}, reconnects={Reconnects})";
}

/// <summary>Two bursts of concurrent allocations for a single Authorization_Key.</summary>
public sealed record QtsBurstCase(int FirstBurst, int SecondBurst)
{
    public override string ToString() => $"Burst(first={FirstBurst}, second={SecondBurst})";
}

/// <summary>An arbitrary interleaving of allocations across the key set, as key indices.</summary>
public sealed record QtsInterleavingCase(int[] Picks)
{
    public override string ToString() => $"Interleaving([{string.Join(",", Picks)}])";
}

/// <summary>One recipient bitmask per fanned-out update (always non-empty).</summary>
public sealed record QtsFanOutCase(int[] RecipientMasks)
{
    public override string ToString() => $"FanOut([{string.Join(",", RecipientMasks)}])";
}

/// <summary>Generators for the qts-sequencer properties.</summary>
public static class QtsGen
{
    /// <summary>Number of distinct Authorization_Keys used by the independence properties.</summary>
    public const int KeyCount = 4;

    public static Gen<QtsSequenceCase> SequenceCase =>
        from allocations in Gen.Choose(1, 12)
        from keySalt in Gen.Choose(1, 4096)
        select new QtsSequenceCase(allocations, keySalt);

    public static Gen<QtsReconnectCase> ReconnectCase =>
        from before in Gen.Choose(1, 6)
        from after in Gen.Choose(1, 6)
        from reconnects in Gen.Choose(1, 3)
        select new QtsReconnectCase(before, after, reconnects);

    /// <summary>The second burst may be empty, so the single-burst case stays reachable.</summary>
    public static Gen<QtsBurstCase> BurstCase =>
        from first in Gen.Choose(2, 32)
        from second in Gen.Choose(0, 16)
        select new QtsBurstCase(first, second);

    public static Gen<QtsInterleavingCase> InterleavingCase =>
        from length in Gen.Choose(4, 40)
        from picks in Gen.ArrayOf(length, Gen.Choose(0, KeyCount - 1))
        select new QtsInterleavingCase(picks);

    /// <summary>Masks are drawn from 1..2^KeyCount-1, so every update has at least one recipient.</summary>
    public static Gen<QtsFanOutCase> FanOutCase =>
        from updates in Gen.Choose(1, 8)
        from masks in Gen.ArrayOf(updates, Gen.Choose(1, (1 << KeyCount) - 1))
        select new QtsFanOutCase(masks);
}

/// <summary>
/// FsCheck arbitrary registration surface for the qts-sequencer properties. The facts cannot carry
/// <c>[Property]</c> (they need a real MongoDB via <c>[RequiresMongoDbFact]</c>), so they sample these
/// arbitraries' generators directly.
/// </summary>
public static class QtsArbitraries
{
    public static Arbitrary<QtsSequenceCase> SequenceCase() => Arb.From(QtsGen.SequenceCase);

    public static Arbitrary<QtsReconnectCase> ReconnectCase() => Arb.From(QtsGen.ReconnectCase);

    public static Arbitrary<QtsBurstCase> BurstCase() => Arb.From(QtsGen.BurstCase);

    public static Arbitrary<QtsInterleavingCase> InterleavingCase() => Arb.From(QtsGen.InterleavingCase);

    public static Arbitrary<QtsFanOutCase> FanOutCase() => Arb.From(QtsGen.FanOutCase);
}
