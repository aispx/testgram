using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Queries;
using MyTelegram.QueryHandlers.MongoDB.Messaging;
using MyTelegram.ReadModel.MongoDB;

namespace MyTelegram.Messenger.Tests.Pin;

/// <summary>
/// Feature: pinned messages — unpinning everything in a chat.
///
/// <para>
/// messages.unpinAllMessages carries <c>top_msg_id</c> and <c>saved_peer_id</c>: with either of them
/// set only the pinned messages of that forum/monoforum topic may be unpinned, never the whole chat.
/// The <c>offset</c> of the returned messages.affectedHistory drives the client loop: non-zero means
/// "call me again", zero means the chat is done.
/// See https://corefork.telegram.org/method/messages.unpinAllMessages
/// </para>
/// </summary>
public class UnpinAllScopeTests
{
    private const long ChannelId = 800000000001;
    private const long SelfUserId = 2010001;
    private const long OtherUserId = 2010002;
    private const long MonoforumPeerId = 2010003;

    [RequiresMongoDbFact]
    public async Task Unpinning_a_forum_topic_leaves_the_pins_of_other_topics_alone()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var messages = mongo.Database.GetCollection<MessageReadModel>("eventflow-messagereadmodel");
        var documents = mongo.Database.GetCollection<BsonDocument>("eventflow-messagereadmodel");

        await documents.InsertManyAsync([
            ChannelMessage(messageId: 10, pinned: true, topMsgId: 5),
            ChannelMessage(messageId: 11, pinned: true, topMsgId: 5),
            ChannelMessage(messageId: 20, pinned: true, topMsgId: 7),
            ChannelMessage(messageId: 30, pinned: false, topMsgId: 5)
        ]);

        var predicate = GetSimpleMessageListQueryHandler.BuildPredicate(new GetSimpleMessageListQuery(
            ChannelId,
            new Peer(PeerType.Channel, ChannelId),
            MessageIds: null,
            Pinned: true,
            IncludeOtherParticipantMessages: true,
            Limit: 500,
            TopMsgId: 5));

        var found = await FindMessageIdsAsync(messages, predicate);

        found.ShouldBe([10, 11]);
    }

    [RequiresMongoDbFact]
    public async Task Unpinning_a_monoforum_topic_is_scoped_by_the_saved_peer()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var messages = mongo.Database.GetCollection<MessageReadModel>("eventflow-messagereadmodel");
        var documents = mongo.Database.GetCollection<BsonDocument>("eventflow-messagereadmodel");

        await documents.InsertManyAsync([
            ChannelMessage(messageId: 40, pinned: true, savedPeerId: MonoforumPeerId),
            ChannelMessage(messageId: 41, pinned: true, savedPeerId: 2010009),
            ChannelMessage(messageId: 42, pinned: true)
        ]);

        var predicate = GetSimpleMessageListQueryHandler.BuildPredicate(new GetSimpleMessageListQuery(
            ChannelId,
            new Peer(PeerType.Channel, ChannelId),
            MessageIds: null,
            Pinned: true,
            IncludeOtherParticipantMessages: true,
            Limit: 500,
            SavedPeerId: new Peer(PeerType.User, MonoforumPeerId)));

        var found = await FindMessageIdsAsync(messages, predicate);

        found.ShouldBe([40]);
    }

    [RequiresMongoDbFact]
    public async Task Without_a_topic_every_pinned_message_of_the_chat_is_matched()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var messages = mongo.Database.GetCollection<MessageReadModel>("eventflow-messagereadmodel");
        var documents = mongo.Database.GetCollection<BsonDocument>("eventflow-messagereadmodel");

        await documents.InsertManyAsync([
            ChannelMessage(messageId: 10, pinned: true, topMsgId: 5),
            ChannelMessage(messageId: 20, pinned: true, topMsgId: 7),
            ChannelMessage(messageId: 30, pinned: false)
        ]);

        var predicate = GetSimpleMessageListQueryHandler.BuildPredicate(new GetSimpleMessageListQuery(
            ChannelId,
            new Peer(PeerType.Channel, ChannelId),
            MessageIds: null,
            Pinned: true,
            IncludeOtherParticipantMessages: true,
            Limit: 500));

        var found = await FindMessageIdsAsync(messages, predicate);

        found.ShouldBe([10, 20]);
    }

    [RequiresMongoDbFact]
    public async Task A_private_chat_is_matched_through_the_callers_own_copy_of_the_history()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var messages = mongo.Database.GetCollection<MessageReadModel>("eventflow-messagereadmodel");
        var documents = mongo.Database.GetCollection<BsonDocument>("eventflow-messagereadmodel");

        await documents.InsertManyAsync([
            PrivateMessage(messageId: 1, ownerPeerId: SelfUserId, toPeerId: OtherUserId, pinned: true),
            // The other side's copy of the same chat, and a pinned message of another chat.
            PrivateMessage(messageId: 2, ownerPeerId: OtherUserId, toPeerId: SelfUserId, pinned: true),
            PrivateMessage(messageId: 3, ownerPeerId: SelfUserId, toPeerId: 2010009, pinned: true)
        ]);

        var predicate = GetSimpleMessageListQueryHandler.BuildPredicate(new GetSimpleMessageListQuery(
            SelfUserId,
            new Peer(PeerType.User, OtherUserId),
            MessageIds: null,
            Pinned: true,
            IncludeOtherParticipantMessages: false,
            Limit: 500));

        var found = await FindMessageIdsAsync(messages, predicate);

        found.ShouldBe([1]);
    }

    [Fact]
    public void The_last_page_reports_offset_zero_so_the_client_stops_calling()
    {
        var lastBatch = PinPagingHelper.IsLastBatch(3);

        lastBatch.ShouldBeTrue();
        PinPagingHelper.CalculateOffset(lastBatch, [10, 20, 30]).ShouldBe(0);
    }

    [Fact]
    public void A_full_page_reports_the_highest_message_id_so_the_client_calls_again()
    {
        var lastBatch = PinPagingHelper.IsLastBatch(MyTelegramConsts.UnPinAllMessagesDefaultPageSize);

        lastBatch.ShouldBeFalse();
        PinPagingHelper.CalculateOffset(lastBatch, [10, 30, 20]).ShouldBe(30);
    }

    private static async Task<List<int>> FindMessageIdsAsync(IMongoCollection<MessageReadModel> collection,
        System.Linq.Expressions.Expression<Func<MessageReadModel, bool>> predicate)
    {
        var found = await collection.Find(predicate)
            .Project(Builders<MessageReadModel>.Projection.Include("MessageId"))
            .ToListAsync();

        return found.Select(p => p["MessageId"].AsInt32).OrderBy(p => p).ToList();
    }

    private static BsonDocument ChannelMessage(int messageId, bool pinned, int? topMsgId = null,
        long? savedPeerId = null)
    {
        var document = new BsonDocument
        {
            { "_id", $"{ChannelId}_{messageId}" },
            { "OwnerPeerId", ChannelId },
            { "ToPeerType", (int)PeerType.Channel },
            { "ToPeerId", ChannelId },
            { "MessageId", messageId },
            { "Pinned", pinned }
        };

        if (topMsgId.HasValue)
        {
            document["TopMsgId"] = topMsgId.Value;
        }

        if (savedPeerId.HasValue)
        {
            document["SavedPeerId"] = new BsonDocument
            {
                { "PeerType", (int)PeerType.User },
                { "PeerId", savedPeerId.Value }
            };
        }

        return document;
    }

    private static BsonDocument PrivateMessage(int messageId, long ownerPeerId, long toPeerId, bool pinned)
    {
        return new BsonDocument
        {
            { "_id", $"{ownerPeerId}_{messageId}" },
            { "OwnerPeerId", ownerPeerId },
            { "ToPeerType", (int)PeerType.User },
            { "ToPeerId", toPeerId },
            { "MessageId", messageId },
            { "Pinned", pinned }
        };
    }
}
