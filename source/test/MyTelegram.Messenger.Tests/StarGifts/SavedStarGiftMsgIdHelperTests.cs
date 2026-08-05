using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Messenger.Tests.Stats;

namespace MyTelegram.Messenger.Tests.StarGifts;

/// <summary>
/// Feature: star gifts — addressing a saved gift by <c>msg_id</c>.
///
/// <para>
/// Layer 229 addresses user-owned gifts through <c>inputSavedStarGiftUser#69279795 msg_id:int</c> —
/// a 32-bit field. Gifts that were never anchored to a service message have <c>MessageId == 0</c>,
/// and the handler used to put the 64-bit <c>RandomId</c> in that slot, where the cast truncated it:
/// <c>6987717616945285832</c> reached the client as <c>1824737992</c>. The client then echoed back an
/// id matching no gift, so every action on a non-upgraded gift silently failed. These tests run
/// against a real <c>mongod</c> because the fix is the interaction between the id counter, the
/// persisted local id and the lookup filter.
/// </para>
/// </summary>
public class SavedStarGiftMsgIdHelperTests
{
    private const long OwnerUserId = 2010001;

    /// <summary>A RandomId whose low 32 bits differ from the value itself.</summary>
    private const long TruncatingRandomId = 6987717616945285832;

    private static SavedStarGiftDocument NewGift(long randomId, int messageId = 0) => new()
    {
        Id = ObjectId.GenerateNewId(),
        OwnerUserId = OwnerUserId,
        MessageId = messageId,
        GiftId = 1,
        Stars = 100,
        RandomId = randomId,
        Saved = true
    };

    [RequiresMongoDbFact]
    public async Task Unanchored_gift_gets_a_msg_id_that_survives_a_32_bit_field()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = mongo.Database.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
        var gift = NewGift(TruncatingRandomId);
        await collection.InsertOneAsync(gift);

        var msgId = await SavedStarGiftMsgIdHelper.ResolveAsync(mongo.Database, gift);

        // The whole point: the advertised id must not be a truncation of RandomId.
        msgId.ShouldNotBe(unchecked((int)TruncatingRandomId));
        msgId.ShouldBeGreaterThan(0);
    }

    [RequiresMongoDbFact]
    public async Task A_message_anchored_gift_keeps_its_real_message_id()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = mongo.Database.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
        var gift = NewGift(TruncatingRandomId, messageId: 4242);
        await collection.InsertOneAsync(gift);

        var msgId = await SavedStarGiftMsgIdHelper.ResolveAsync(mongo.Database, gift);

        msgId.ShouldBe(4242);
        // No local id is burned for a gift that already has a real anchor.
        var stored = await collection.Find(d => d.Id == gift.Id).FirstAsync();
        stored.LocalMsgId.ShouldBe(0);
    }

    [RequiresMongoDbFact]
    public async Task The_allocated_msg_id_is_persisted_and_stable_across_calls()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = mongo.Database.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
        var gift = NewGift(TruncatingRandomId);
        await collection.InsertOneAsync(gift);

        var first = await SavedStarGiftMsgIdHelper.ResolveAsync(mongo.Database, gift);

        // Re-read from the database so nothing is carried over in memory: the id has to come back
        // from the document, otherwise a restart would hand the client a different id.
        var reloaded = await collection.Find(d => d.Id == gift.Id).FirstAsync();
        reloaded.LocalMsgId.ShouldBe(first);

        var second = await SavedStarGiftMsgIdHelper.ResolveAsync(mongo.Database, reloaded);
        second.ShouldBe(first);
    }

    [RequiresMongoDbFact]
    public async Task Distinct_gifts_get_distinct_msg_ids()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = mongo.Database.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
        var gifts = new[]
        {
            NewGift(6987717616945285832),
            NewGift(5618950752844533985),
            NewGift(5792115637782422712),
            NewGift(5790684370393538560)
        };
        await collection.InsertManyAsync(gifts);

        var ids = new List<int>();
        foreach (var gift in gifts)
            ids.Add(await SavedStarGiftMsgIdHelper.ResolveAsync(mongo.Database, gift));

        ids.Distinct().Count().ShouldBe(gifts.Length);
    }

    [RequiresMongoDbFact]
    public async Task A_gift_can_be_found_again_by_the_msg_id_it_advertised()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = mongo.Database.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
        var gift = NewGift(TruncatingRandomId);
        await collection.InsertOneAsync(gift);

        // Round-trip: hand out an id, then resolve it the way the action handlers do.
        var msgId = await SavedStarGiftMsgIdHelper.ResolveAsync(mongo.Database, gift);
        var found = await collection.Find(
            Builders<SavedStarGiftDocument>.Filter.Eq(d => d.OwnerUserId, OwnerUserId)
            & SavedStarGiftMsgIdHelper.MatchMsgId(msgId)).FirstOrDefaultAsync();

        found.ShouldNotBeNull();
        found.Id.ShouldBe(gift.Id);
    }

    [RequiresMongoDbFact]
    public async Task The_truncated_random_id_no_longer_resolves_to_anything()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = mongo.Database.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
        var gift = NewGift(TruncatingRandomId);
        await collection.InsertOneAsync(gift);
        await SavedStarGiftMsgIdHelper.ResolveAsync(mongo.Database, gift);

        // This is the id the old code sent to the client. It must not match, which is exactly why
        // convert/upgrade/transfer used to fail — and why we stopped advertising it.
        var truncated = unchecked((int)TruncatingRandomId);
        var found = await collection.Find(
            Builders<SavedStarGiftDocument>.Filter.Eq(d => d.OwnerUserId, OwnerUserId)
            & SavedStarGiftMsgIdHelper.MatchMsgId(truncated)).FirstOrDefaultAsync();

        found.ShouldBeNull();
    }

    [RequiresMongoDbFact]
    public async Task Upgraded_gifts_are_still_addressable_by_their_small_unique_id()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = mongo.Database.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
        // Unique gifts store a small sequential id in RandomId, which fits in 32 bits and is what
        // clients already hold. Those must keep resolving.
        var unique = NewGift(1133);
        unique.IsUnique = true;
        unique.UniqueSlug = "blue-star-132";
        await collection.InsertOneAsync(unique);

        var found = await collection.Find(
            Builders<SavedStarGiftDocument>.Filter.Eq(d => d.OwnerUserId, OwnerUserId)
            & SavedStarGiftMsgIdHelper.MatchMsgId(1133)).FirstOrDefaultAsync();

        found.ShouldNotBeNull();
        found.UniqueSlug.ShouldBe("blue-star-132");
    }

    [RequiresMongoDbFact]
    public async Task A_local_id_cannot_collide_with_a_real_message_id()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = mongo.Database.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
        var anchored = NewGift(1, messageId: 5000);
        var unanchored = NewGift(TruncatingRandomId);
        await collection.InsertManyAsync([anchored, unanchored]);

        var localId = await SavedStarGiftMsgIdHelper.ResolveAsync(mongo.Database, unanchored);

        // Local ids live far above realistic message ids, so resolving one never returns the
        // message-anchored gift.
        localId.ShouldBeGreaterThan(anchored.MessageId);
        var found = await collection.Find(
            Builders<SavedStarGiftDocument>.Filter.Eq(d => d.OwnerUserId, OwnerUserId)
            & SavedStarGiftMsgIdHelper.MatchMsgId(localId)).FirstOrDefaultAsync();
        found!.Id.ShouldBe(unanchored.Id);
    }

    [RequiresMongoDbFact]
    public async Task Documents_written_before_the_field_existed_still_work()
    {
        using var mongo = EmbeddedMongoServer.Start();
        // Every gift already in the database predates LocalMsgId, so the field is simply absent
        // there. Insert that exact shape as a raw document rather than through the typed class.
        var raw = mongo.Database.GetCollection<BsonDocument>("saved-star-gifts");
        var id = ObjectId.GenerateNewId();
        await raw.InsertOneAsync(new BsonDocument
        {
            { "_id", id },
            { "OwnerUserId", OwnerUserId },
            { "MessageId", 0 },
            { "GiftId", 1L },
            { "Stars", 100L },
            { "RandomId", TruncatingRandomId },
            { "Saved", true },
            { "IsUnique", false }
        });

        var collection = mongo.Database.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
        var legacy = await collection.Find(d => d.Id == id).FirstAsync();
        legacy.LocalMsgId.ShouldBe(0);

        // A missing field must not match the "already allocated" branch, and allocating must
        // upgrade the document in place.
        var msgId = await SavedStarGiftMsgIdHelper.ResolveAsync(mongo.Database, legacy);
        msgId.ShouldNotBe(unchecked((int)TruncatingRandomId));

        var found = await collection.Find(
            Builders<SavedStarGiftDocument>.Filter.Eq(d => d.OwnerUserId, OwnerUserId)
            & SavedStarGiftMsgIdHelper.MatchMsgId(msgId)).FirstOrDefaultAsync();
        found!.Id.ShouldBe(id);
    }

    [RequiresMongoDbFact]
    public async Task An_unallocated_gift_is_not_matched_by_a_zero_msg_id()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = mongo.Database.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
        // MessageId and LocalMsgId are both 0 here. A msg_id of 0 must not match on either, or a
        // client sending nothing would silently act on an arbitrary gift.
        await collection.InsertOneAsync(NewGift(TruncatingRandomId));

        var found = await collection.Find(
            Builders<SavedStarGiftDocument>.Filter.Eq(d => d.OwnerUserId, OwnerUserId)
            & SavedStarGiftMsgIdHelper.MatchMsgId(0)).FirstOrDefaultAsync();

        found.ShouldBeNull();
    }
}
