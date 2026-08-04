using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Handlers.LatestLayer.Contacts;
using MyTelegram.Messenger.Tests.Stats;

namespace MyTelegram.Messenger.Tests.Search;

/// <summary>
/// Feature: the <a href="https://corefork.telegram.org/api/top-rating">top peers rating</a> that
/// pre-populates the chats tab of global search.
///
/// <para>
/// There is no usage counter in the read models, so the rating is derived from the caller's outgoing
/// messages: frequency decayed by how long ago the conversation last happened. These tests run
/// against a real <c>mongod</c> because the ranking is an aggregation, and because enums are stored
/// as their numeric value — a string comparison there silently matches nothing.
/// </para>
/// </summary>
public class TopPeerRatingHelperTests
{
    private const long SelfUserId = 777;
    private const string MessageCollection = "eventflow-messagereadmodel";

    [RequiresMongoDbFact]
    public async Task A_user_with_no_history_has_no_rating()
    {
        using var mongo = EmbeddedMongoServer.Start();

        var ratings = await TopPeerRatingHelper.GetRatingsAsync(mongo.Database, SelfUserId, Now());

        ratings.ShouldBeEmpty();
    }

    [RequiresMongoDbFact]
    public async Task Peers_are_ranked_by_how_often_we_message_them()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var now = Now();

        await AddOutgoingMessagesAsync(mongo.Database, peerId: 10, count: 2, date: now - 60);
        await AddOutgoingMessagesAsync(mongo.Database, peerId: 20, count: 9, date: now - 60);

        var ratings = await TopPeerRatingHelper.GetRatingsAsync(mongo.Database, SelfUserId, now);

        ratings.Count.ShouldBe(2);
        ratings[0].PeerId.ShouldBe(20);
        ratings[1].PeerId.ShouldBe(10);
    }

    [RequiresMongoDbFact]
    public async Task A_recent_conversation_outranks_an_equally_busy_old_one()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var now = Now();

        await AddOutgoingMessagesAsync(mongo.Database, peerId: 10, count: 5, date: now - 60 * 60);
        await AddOutgoingMessagesAsync(mongo.Database, peerId: 20, count: 5, date: now - 60 * 24 * 60 * 60);

        var ratings = await TopPeerRatingHelper.GetRatingsAsync(mongo.Database, SelfUserId, now);

        ratings[0].PeerId.ShouldBe(10);
        ratings[0].Rating.ShouldBeGreaterThan(ratings[1].Rating);
    }

    [RequiresMongoDbFact]
    public async Task Incoming_messages_and_other_peoples_chats_do_not_count()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var now = Now();

        await AddOutgoingMessagesAsync(mongo.Database, peerId: 10, count: 1, date: now - 60);
        // Received, not sent.
        await AddMessageAsync(mongo.Database, SelfUserId, peerId: 11, date: now - 60, isOut: false);
        // Someone else's dialog.
        await AddMessageAsync(mongo.Database, ownerPeerId: 999, peerId: 12, date: now - 60, isOut: true);

        var ratings = await TopPeerRatingHelper.GetRatingsAsync(mongo.Database, SelfUserId, now);

        ratings.Select(p => p.PeerId).ShouldBe([10]);
    }

    [RequiresMongoDbFact]
    public async Task Messages_older_than_the_rating_window_are_ignored()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var now = Now();

        await AddOutgoingMessagesAsync(mongo.Database, peerId: 10, count: 3, date: now - 200 * 24 * 60 * 60);

        var ratings = await TopPeerRatingHelper.GetRatingsAsync(mongo.Database, SelfUserId, now);

        ratings.ShouldBeEmpty();
    }

    [RequiresMongoDbFact]
    public async Task A_reset_peer_stays_out_of_the_rating()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var now = Now();

        await AddOutgoingMessagesAsync(mongo.Database, peerId: 10, count: 4, date: now - 60);
        await AddOutgoingMessagesAsync(mongo.Database, peerId: 20, count: 1, date: now - 60);

        await TopPeerRatingHelper.ExcludePeerAsync(mongo.Database, SelfUserId, PeerType.User, 10);

        var ratings = await TopPeerRatingHelper.GetRatingsAsync(mongo.Database, SelfUserId, now);

        // Resetting is remembered, otherwise the peer would come straight back on the next message.
        ratings.Select(p => p.PeerId).ShouldBe([20]);
    }

    [RequiresMongoDbFact]
    public async Task Top_peers_can_be_disabled_and_re_enabled()
    {
        using var mongo = EmbeddedMongoServer.Start();

        (await TopPeerRatingHelper.IsDisabledAsync(mongo.Database, SelfUserId)).ShouldBeFalse();

        await TopPeerRatingHelper.SetDisabledAsync(mongo.Database, SelfUserId, true);
        (await TopPeerRatingHelper.IsDisabledAsync(mongo.Database, SelfUserId)).ShouldBeTrue();

        await TopPeerRatingHelper.SetDisabledAsync(mongo.Database, SelfUserId, false);
        (await TopPeerRatingHelper.IsDisabledAsync(mongo.Database, SelfUserId)).ShouldBeFalse();
    }

    [RequiresMongoDbFact]
    public async Task Phone_calls_are_flagged_so_they_can_form_their_own_category()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var now = Now();

        await AddMessageAsync(mongo.Database, SelfUserId, peerId: 10, date: now - 60, isOut: true,
            messageType: MessageType.PhoneCall);
        await AddMessageAsync(mongo.Database, SelfUserId, peerId: 20, date: now - 60, isOut: true);

        var ratings = await TopPeerRatingHelper.GetRatingsAsync(mongo.Database, SelfUserId, now);

        ratings.Single(p => p.PeerId == 10).IsPhoneCall.ShouldBeTrue();
        ratings.Single(p => p.PeerId == 20).IsPhoneCall.ShouldBeFalse();
    }

    private static int Now()
    {
        return (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    private static async Task AddOutgoingMessagesAsync(IMongoDatabase database, long peerId, int count, int date)
    {
        for (var i = 0; i < count; i++)
        {
            await AddMessageAsync(database, SelfUserId, peerId, date, isOut: true);
        }
    }

    private static Task AddMessageAsync(IMongoDatabase database, long ownerPeerId, long peerId, int date, bool isOut,
        MessageType messageType = MessageType.Text)
    {
        return database.GetCollection<BsonDocument>(MessageCollection).InsertOneAsync(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "OwnerPeerId", ownerPeerId },
            { "Out", isOut },
            { "Date", date },
            // Enums are persisted as their numeric value, not as their name.
            { "ToPeerType", (int)PeerType.User },
            { "ToPeerId", peerId },
            { "MessageType", (int)messageType }
        });
    }
}
