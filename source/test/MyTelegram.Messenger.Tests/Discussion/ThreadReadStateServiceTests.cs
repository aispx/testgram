using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Discussion;
using MyTelegram.Messenger.Tests.Stats;

namespace MyTelegram.Messenger.Tests.Discussion;

/// <summary>
/// Feature: discussion groups — per-thread read state.
///
/// <para>
/// A comment section has a read pointer of its own: reading the comments of one channel post must not
/// mark the rest of the discussion group read, and the group dialog's read state says nothing about an
/// individual thread. These tests run against a real <c>mongod</c> because the behaviour under test is
/// the monotonic upsert and the unread count query, both of which live in the database.
/// See https://corefork.telegram.org/api/threads
/// </para>
/// </summary>
public class ThreadReadStateServiceTests
{
    private const long UserId = 100;
    private const long ChannelId = 800000000001;
    private const int TopMsgId = 5;

    [RequiresMongoDbFact]
    public async Task Inbox_pointer_only_moves_forward()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = new ThreadReadStateService(mongo.Database);

        (await service.SetInboxAsync(UserId, ChannelId, TopMsgId, 10)).ShouldBeTrue();

        // Re-reading the same thread at or below the stored pointer is a no-op, which is what lets
        // messages.readDiscussion answer boolFalse instead of pushing a pointless update.
        (await service.SetInboxAsync(UserId, ChannelId, TopMsgId, 10)).ShouldBeFalse();
        (await service.SetInboxAsync(UserId, ChannelId, TopMsgId, 4)).ShouldBeFalse();

        var state = await service.GetAsync(UserId, ChannelId, TopMsgId);
        state.ShouldNotBeNull();
        state!.ReadInboxMaxId.ShouldBe(10);
    }

    [RequiresMongoDbFact]
    public async Task Read_state_is_scoped_to_one_thread_and_one_user()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = new ThreadReadStateService(mongo.Database);

        await service.SetInboxAsync(UserId, ChannelId, TopMsgId, 10);

        (await service.GetAsync(UserId, ChannelId, TopMsgId + 1)).ShouldBeNull();
        (await service.GetAsync(UserId + 1, ChannelId, TopMsgId)).ShouldBeNull();
        (await service.GetAsync(UserId, ChannelId + 1, TopMsgId)).ShouldBeNull();
    }

    [RequiresMongoDbFact]
    public async Task Inbox_and_outbox_pointers_are_independent()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = new ThreadReadStateService(mongo.Database);

        await service.SetInboxAsync(UserId, ChannelId, TopMsgId, 10);
        await service.SetOutboxAsync(UserId, ChannelId, TopMsgId, 7);

        var state = await service.GetAsync(UserId, ChannelId, TopMsgId);
        state!.ReadInboxMaxId.ShouldBe(10);
        state.ReadOutboxMaxId.ShouldBe(7);
    }

    [RequiresMongoDbFact]
    public async Task GetMany_returns_the_state_of_every_requested_thread()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = new ThreadReadStateService(mongo.Database);

        await service.SetInboxAsync(UserId, ChannelId, TopMsgId, 10);
        await service.SetInboxAsync(UserId, ChannelId, TopMsgId + 1, 20);

        var states = await service.GetManyAsync(UserId,
            [(ChannelId, TopMsgId), (ChannelId, TopMsgId + 1), (ChannelId, 999)]);

        states.Count.ShouldBe(2);
        states[IThreadReadStateService.Key(ChannelId, TopMsgId)].ReadInboxMaxId.ShouldBe(10);
        states[IThreadReadStateService.Key(ChannelId, TopMsgId + 1)].ReadInboxMaxId.ShouldBe(20);
    }

    [RequiresMongoDbFact]
    public async Task Unread_count_covers_nested_replies_and_skips_our_own_messages()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = new ThreadReadStateService(mongo.Database);
        var messages = mongo.Database.GetCollection<BsonDocument>("eventflow-messagereadmodel");

        // The thread root, a direct reply, a reply to that reply, and one message of our own.
        await messages.InsertManyAsync([
            Message(messageId: TopMsgId, senderUserId: 200, replyToMsgId: null, topMsgId: null),
            Message(messageId: 6, senderUserId: 200, replyToMsgId: TopMsgId, topMsgId: null),
            Message(messageId: 7, senderUserId: 300, replyToMsgId: 6, topMsgId: TopMsgId),
            Message(messageId: 8, senderUserId: UserId, replyToMsgId: TopMsgId, topMsgId: null),
            // A message of the same group but outside the thread must not be counted.
            Message(messageId: 9, senderUserId: 200, replyToMsgId: null, topMsgId: null)
        ]);

        (await service.GetUnreadCountAsync(ChannelId, TopMsgId, 0, UserId)).ShouldBe(2);

        // After reading up to the direct reply only the nested one is left.
        (await service.GetUnreadCountAsync(ChannelId, TopMsgId, 6, UserId)).ShouldBe(1);
    }

    [RequiresMongoDbFact]
    public async Task Reading_a_thread_advances_the_outbox_pointer_of_the_authors_that_were_read()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = new ThreadReadStateService(mongo.Database);
        var messages = mongo.Database.GetCollection<BsonDocument>("eventflow-messagereadmodel");

        await messages.InsertManyAsync([
            Message(messageId: 6, senderUserId: 200, replyToMsgId: TopMsgId, topMsgId: null),
            Message(messageId: 7, senderUserId: 300, replyToMsgId: 6, topMsgId: TopMsgId),
            // Above the read pointer: its author must not be told anything yet.
            Message(messageId: 12, senderUserId: 400, replyToMsgId: TopMsgId, topMsgId: null)
        ]);

        var affected = await service.MarkOutboxReadAsync(ChannelId, TopMsgId, readMaxId: 7, readerUserId: UserId);

        affected.OrderBy(p => p).ShouldBe([200L, 300L]);
        (await service.GetAsync(200, ChannelId, TopMsgId))!.ReadOutboxMaxId.ShouldBe(7);
        (await service.GetAsync(400, ChannelId, TopMsgId)).ShouldBeNull();
    }

    [RequiresMongoDbFact]
    public async Task The_reader_is_never_told_that_their_own_messages_were_read()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = new ThreadReadStateService(mongo.Database);
        var messages = mongo.Database.GetCollection<BsonDocument>("eventflow-messagereadmodel");

        await messages.InsertOneAsync(Message(messageId: 6, senderUserId: UserId, replyToMsgId: TopMsgId, topMsgId: null));

        var affected = await service.MarkOutboxReadAsync(ChannelId, TopMsgId, readMaxId: 10, readerUserId: UserId);

        affected.ShouldBeEmpty();
    }

    private static BsonDocument Message(int messageId, long senderUserId, int? replyToMsgId, int? topMsgId)
    {
        return new BsonDocument
        {
            { "_id", $"{ChannelId}-{messageId}" },
            { "OwnerPeerId", ChannelId },
            { "MessageId", messageId },
            { "SenderUserId", senderUserId },
            { "ReplyToMsgId", replyToMsgId.HasValue ? replyToMsgId.Value : BsonNull.Value },
            { "TopMsgId", topMsgId.HasValue ? topMsgId.Value : BsonNull.Value }
        };
    }
}
