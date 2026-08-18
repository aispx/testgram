using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Abstractions;
using MyTelegram.Messenger.Handlers.LatestLayer.Messages;
using MyTelegram.Messenger.Services;
using MyTelegram.Messenger.TLObjectConverters.Mappers;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.Discussion;

/// <summary>
/// Feature: message threads — reading one thread out of a chat.
///
/// <para>
/// A thread holds every message whose reply chain leads back to the root: a direct reply carries the
/// root in <c>reply_to_msg_id</c>, a reply to a comment carries it in <c>top_msg_id</c>. Every read
/// path scoped by <c>top_msg_id</c> (messages.getReplies, messages.search,
/// messages.getSearchCounters) has to match both legs, and messages.getReplies pages exactly like
/// messages.getHistory. See https://corefork.telegram.org/api/threads
/// </para>
/// </summary>
public class ThreadScopedQueryTests
{
    private const long ChannelId = 800000000001;
    private const int RootMessageId = 420;

    [RequiresMongoDbFact]
    public async Task Search_filter_scoped_to_a_thread_matches_direct_and_nested_replies()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var messages = mongo.Database.GetCollection<BsonDocument>("eventflow-messagereadmodel");

        await messages.InsertManyAsync([
            Message(messageId: RootMessageId, replyToMsgId: null, topMsgId: null),
            Message(messageId: 421, replyToMsgId: RootMessageId, topMsgId: null),
            Message(messageId: 422, replyToMsgId: 421, topMsgId: RootMessageId),
            // Another thread of the same chat, plus a message outside any thread.
            Message(messageId: 500, replyToMsgId: 499, topMsgId: 499),
            Message(messageId: 501, replyToMsgId: null, topMsgId: null)
        ]);

        var filter = MessageSearchMongoHelper.BuildFilter(
            RequestInput(),
            new PeerHelper(),
            new TInputPeerChannel { ChannelId = ChannelId, AccessHash = 0 },
            savedPeerInput: null,
            topMsgId: RootMessageId,
            filter: null);

        var found = await messages.Find(filter).ToListAsync();

        found.Select(p => p["MessageId"].AsInt32).OrderBy(p => p).ShouldBe([421, 422]);
    }

    [Fact]
    public void A_thread_page_is_bounded_by_max_id_min_id_and_offset_date()
    {
        // messages.getReplies carries max_id/min_id/offset_date; dropping them on the way to the read
        // model returned an unrelated page whenever a client filled a hole in a comment section.
        var query = new CustomObjectMapper().Map(new GetRepliesInput
        {
            ReplyToMsgId = RootMessageId,
            OwnerPeerId = ChannelId,
            Limit = 20,
            MaxId = 500,
            MinId = 421,
            MaxDate = 1700000000
        });

        query.ShouldNotBeNull();
        query!.ReplyToMsgId.ShouldBe(RootMessageId);
        query.MaxId.ShouldBe(500);
        query.MinId.ShouldBe(421);
        query.MaxDate.ShouldBe(1700000000);
    }

    private static IRequestInput RequestInput()
    {
        return new RequestInput(
            "connection-id",
            ConnectionType.Generic,
            Guid.NewGuid(),
            0u,
            0,
            0,
            UserId: 100,
            AuthKeyId: 0,
            PermAuthKeyId: 0,
            Layer: 222,
            Date: 0,
            DeviceType.Android,
            "127.0.0.1",
            SessionId: 0,
            AccessHashKeyId: 0);
    }

    private static BsonDocument Message(int messageId, int? replyToMsgId, int? topMsgId)
    {
        return new BsonDocument
        {
            { "_id", $"{ChannelId}-{messageId}" },
            { "OwnerPeerId", ChannelId },
            { "ToPeerType", (int)PeerType.Channel },
            { "ToPeerId", ChannelId },
            { "MessageId", messageId },
            { "ReplyToMsgId", replyToMsgId.HasValue ? replyToMsgId.Value : BsonNull.Value },
            { "TopMsgId", topMsgId.HasValue ? topMsgId.Value : BsonNull.Value }
        };
    }
}
