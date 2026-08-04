using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Handlers.LatestLayer.Messages;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// Shared plumbing of the <c>account.get*EmojiStatuses</c> methods: the sticker sets a
/// <a href="https://corefork.telegram.org/api/emoji-status">status</a> may come from, and the
/// <a href="https://corefork.telegram.org/api/offsets#hash-generation">hash</a> clients send to skip
/// an unchanged response.
/// </summary>
internal static class EmojiStatusesHelper
{
    /// <summary>
    /// Custom emoji document IDs of the sticker sets matching <paramref name="filter"/>, in set order.
    /// </summary>
    public static async Task<List<long>> GetDocumentIdsAsync(
        IMongoDatabase mongoDatabase,
        FilterDefinition<BsonDocument> filter)
    {
        var sets = await mongoDatabase.GetCollection<BsonDocument>("eventflow-stickersetreadmodel")
            .Find(filter)
            .Sort(Builders<BsonDocument>.Sort.Ascending("StickerSetId"))
            .Project(Builders<BsonDocument>.Projection.Include("DocumentIds"))
            .ToListAsync();

        return sets
            .Where(p => p.TryGetValue("DocumentIds", out var ids) && ids.IsBsonArray)
            .SelectMany(p => p["DocumentIds"].AsBsonArray.Select(id => id.ToInt64()))
            .Where(id => id != 0)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Hash of a status list, following the
    /// <a href="https://corefork.telegram.org/api/offsets#hash-generation">official algorithm</a> over
    /// the custom emoji IDs, so a client-supplied hash actually matches and
    /// <c>emojiStatusesNotModified</c> can be returned.
    /// </summary>
    public static long CalculateHash(IEnumerable<long> documentIds)
    {
        return documentIds.Aggregate(0L, MessageSearchMongoHelper.CalcHash);
    }

    /// <summary>
    /// Builds the response, returning <c>emojiStatusesNotModified</c> when the client already holds
    /// this exact list.
    /// </summary>
    public static MyTelegram.Schema.Account.IEmojiStatuses ToEmojiStatuses(
        List<long> documentIds,
        long requestHash)
    {
        var hash = CalculateHash(documentIds);
        if (requestHash != 0 && requestHash == hash)
        {
            return new MyTelegram.Schema.Account.TEmojiStatusesNotModified();
        }

        return new MyTelegram.Schema.Account.TEmojiStatuses
        {
            Hash = hash,
            Statuses = new TVector<IEmojiStatus>(
                documentIds.Select(IEmojiStatus (id) => new TEmojiStatus { DocumentId = id }).ToList())
        };
    }
}
