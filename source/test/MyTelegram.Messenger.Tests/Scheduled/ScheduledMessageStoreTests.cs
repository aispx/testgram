using MongoDB.Driver;
using MyTelegram;
using MyTelegram.Messenger.Services;
using MyTelegram.Messenger.Services.Scheduled;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Scheduled;

/// <summary>
/// Feature: scheduled messages — the schedule queue itself.
///
/// <para>
/// A queued message is stored as the very <c>MessageItem</c> the send pipeline built, so media,
/// entities, the reply header and the keyboard survive until the message is finally sent. Entries are
/// claimed with a lease before they are flushed, so two command servers cannot send the same message
/// twice. See https://corefork.telegram.org/api/scheduled-messages
/// </para>
/// </summary>
public class ScheduledMessageStoreTests
{
    private const long UserId = 2010001;
    private const long PeerUserId = 2010002;

    static ScheduledMessageStoreTests()
    {
        ScheduledTestSerializers.EnsureRegistered();
    }

    [RequiresMongoDbFact]
    public async Task A_queued_message_keeps_its_media_entities_reply_and_keyboard()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = mongo.Database.GetCollection<ScheduledMessageDocument>("scheduled_messages");

        var document = Document(scheduledMessageId: 11, scheduleDate: 1_700_000_000, item: Item() with
        {
            Media = new TMessageMediaPhoto { Photo = new TPhoto { Id = 777, AccessHash = 5, FileReference = [1, 2], Sizes = new TVector<IPhotoSize>(), DcId = 2 } },
            Entities = new TVector<IMessageEntity>(new TMessageEntityBold { Offset = 0, Length = 2 }),
            InputReplyTo = new TInputReplyToMessage { ReplyToMsgId = 42 },
            ReplyMarkup = new TReplyInlineMarkup { Rows = new TVector<IKeyboardButtonRow>() },
            GroupId = 909,
            Silent = true,
            NoForwards = true
        });

        await collection.InsertOneAsync(document);

        var loaded = await collection.Find(p => p.Id == document.Id).FirstAsync();

        loaded.Item.Media.ShouldBeOfType<TMessageMediaPhoto>().Photo.ShouldBeOfType<TPhoto>().Id.ShouldBe(777);
        loaded.Item.Entities!.Single().ShouldBeOfType<TMessageEntityBold>().Length.ShouldBe(2);
        loaded.Item.InputReplyTo.ShouldBeOfType<TInputReplyToMessage>().ReplyToMsgId.ShouldBe(42);
        loaded.Item.ReplyMarkup.ShouldBeOfType<TReplyInlineMarkup>();
        loaded.Item.GroupId.ShouldBe(909);
        loaded.Item.Silent.ShouldBeTrue();
        loaded.Item.NoForwards.ShouldBeTrue();
        loaded.ScheduleDate.ShouldBe(1_700_000_000);
    }

    [RequiresMongoDbFact]
    public async Task A_private_queue_only_shows_the_messages_of_the_asking_user()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = CreateStore(mongo.Database);
        var collection = mongo.Database.GetCollection<ScheduledMessageDocument>("scheduled_messages");

        await collection.InsertManyAsync([
            Document(scheduledMessageId: 1, scheduleDate: 1_700_000_100),
            Document(scheduledMessageId: 2, scheduleDate: 1_700_000_200, senderUserId: 2010009),
            // Another peer of the same user.
            Document(scheduledMessageId: 3, scheduleDate: 1_700_000_300, peerId: 2010003)
        ]);

        var peer = new Peer(PeerType.User, PeerUserId);

        var own = await store.GetQueueAsync(peer, UserId, sharedQueue: false);
        own.Select(p => p.ScheduledMessageId).ShouldBe([1]);

        var shared = await store.GetQueueAsync(peer, UserId, sharedQueue: true);
        shared.Select(p => p.ScheduledMessageId).ShouldBe([1, 2]);

        var byId = await store.GetQueueAsync(peer, UserId, sharedQueue: true, [2]);
        byId.Select(p => p.ScheduledMessageId).ShouldBe([2]);
    }

    [RequiresMongoDbFact]
    public async Task Only_due_entries_are_claimed_and_a_claimed_entry_is_not_handed_out_twice()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = CreateStore(mongo.Database);
        var collection = mongo.Database.GetCollection<ScheduledMessageDocument>("scheduled_messages");

        var now = 1_700_000_000;
        await collection.InsertManyAsync([
            Document(scheduledMessageId: 1, scheduleDate: now - 5),
            Document(scheduledMessageId: 2, scheduleDate: now + 3600),
            Document(scheduledMessageId: 3, scheduleDate: ScheduledMessageRules.WhenOnlineDate),
            // Failed earlier, still waiting out its backoff.
            Document(scheduledMessageId: 4, scheduleDate: now - 60, nextAttemptDate: now + 600)
        ]);

        var due = await store.ClaimDueAsync(now, limit: 10, leaseSeconds: 60);
        due.Select(p => p.ScheduledMessageId).ShouldBe([1]);

        var again = await store.ClaimDueAsync(now, limit: 10, leaseSeconds: 60);
        again.ShouldBeEmpty();

        // The when-online entry only fires once its peer is reported online.
        (await store.ClaimWhenOnlineAsync([9999], 10, 60)).ShouldBeEmpty();
        (await store.ClaimWhenOnlineAsync([PeerUserId], 10, 60))
            .Select(p => p.ScheduledMessageId).ShouldBe([3]);

        // The marker date must never be treated as a point in time to wait for.
        (await store.GetNextScheduleDateAsync()).ShouldBe(now - 60);
    }

    [RequiresMongoDbFact]
    public async Task A_released_entry_is_retried_only_after_its_backoff()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = CreateStore(mongo.Database);
        var collection = mongo.Database.GetCollection<ScheduledMessageDocument>("scheduled_messages");

        var now = 1_700_000_000;
        await collection.InsertOneAsync(Document(scheduledMessageId: 1, scheduleDate: now - 5));

        var claimed = (await store.ClaimDueAsync(now, 10, 60)).Single();
        await store.ReleaseAsync(claimed, now + 60);

        (await store.ClaimDueAsync(now, 10, 60)).ShouldBeEmpty();
        (await store.ClaimDueAsync(now + 60, 10, 60)).Select(p => p.Attempts).ShouldBe([1]);
    }

    [RequiresMongoDbFact]
    public async Task An_entry_waiting_for_its_video_waits_for_the_converter_not_for_the_clock()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = CreateStore(mongo.Database);
        var collection = mongo.Database.GetCollection<ScheduledMessageDocument>("scheduled_messages");

        var now = 1_700_000_000;
        var pending = Document(scheduledMessageId: 1, scheduleDate: now - 30);
        pending.VideoProcessingPending = true;
        await collection.InsertManyAsync([pending, Document(scheduledMessageId: 2, scheduleDate: now - 5)]);

        // The estimated conversion date has passed, but the message must not be sent before the
        // alternative qualities exist.
        (await store.ClaimDueAsync(now, 10, 60)).Select(p => p.ScheduledMessageId).ShouldBe([2]);
        (await store.GetNextScheduleDateAsync()).ShouldBe(now - 5);

        (await store.ClaimVideoProcessingAsync(10, 60)).Select(p => p.ScheduledMessageId).ShouldBe([1]);
        (await store.ClaimVideoProcessingAsync(10, 60)).ShouldBeEmpty();
    }

    internal static ScheduledMessageStore CreateStore(IMongoDatabase database)
    {
        // Only the storage half of the store is exercised here; rendering and validation need the
        // converters and app services the handlers inject.
        return new ScheduledMessageStore(database, null!, null!, null!, null!, null!, null!);
    }

    internal static MessageItem Item(long senderUserId = UserId, long peerId = PeerUserId, int messageId = 1)
    {
        return new MessageItem(
            new Peer(PeerType.User, senderUserId),
            new Peer(PeerType.User, peerId),
            new Peer(PeerType.User, senderUserId),
            senderUserId,
            messageId,
            "hi",
            1_699_999_000,
            123456789,
            true);
    }

    internal static ScheduledMessageDocument Document(int scheduledMessageId, int scheduleDate,
        long senderUserId = UserId, long peerId = PeerUserId, int? nextAttemptDate = null, int? repeatPeriod = null,
        MessageItem? item = null)
    {
        return new ScheduledMessageDocument
        {
            Id = ScheduledMessageStore.BuildDocumentId(senderUserId, scheduledMessageId),
            ScheduledMessageId = scheduledMessageId,
            OwnerPeerId = senderUserId,
            SenderUserId = senderUserId,
            PeerId = peerId,
            PeerType = MyTelegram.PeerType.User.ToString(),
            ScheduleDate = scheduleDate,
            RepeatPeriod = repeatPeriod,
            Item = item ?? Item(senderUserId, peerId),
            Layer = 222,
            RandomId = 123456789,
            NextAttemptDate = nextAttemptDate
        };
    }
}
