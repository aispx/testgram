using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Contacts;

/// <summary>
/// Computes the "most used peers" rating backing <c>contacts.getTopPeers</c>, and stores the
/// per-user opt-out and per-peer exclusions used by <c>contacts.toggleTopPeers</c> and
/// <c>contacts.resetTopPeerRating</c>.
/// See https://corefork.telegram.org/api/top-rating
/// </summary>
/// <remarks>
/// There is no dedicated usage counter in the read models, so the rating is derived from the
/// outgoing messages in <c>eventflow-messagereadmodel</c>: recent, frequent conversations rank
/// highest. The exponential decay mirrors the official rating decay described in the docs.
/// </remarks>
internal static class TopPeerRatingHelper
{
    public const string SettingsCollectionName = "top_peers_settings";
    public const string ExcludedCollectionName = "top_peers_excluded";

    private const string MessageCollectionName = "eventflow-messagereadmodel";

    /// <summary>Only messages from the last 90 days contribute to the rating.</summary>
    private const int RatingWindowSeconds = 90 * 24 * 60 * 60;

    /// <summary>Rating half-life: a conversation loses relevance over roughly a month.</summary>
    private const double DecaySeconds = 30d * 24 * 60 * 60;

    internal sealed record PeerRating(PeerType PeerType, long PeerId, double Rating, bool IsPhoneCall, bool IsForward);

    public static async Task<bool> IsDisabledAsync(IMongoDatabase database, long userId)
    {
        var doc = await database.GetCollection<BsonDocument>(SettingsCollectionName)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", userId))
            .FirstOrDefaultAsync();

        return doc != null && doc.GetValue("Disabled", BsonBoolean.False).ToBoolean();
    }

    public static Task SetDisabledAsync(IMongoDatabase database, long userId, bool disabled)
    {
        return database.GetCollection<BsonDocument>(SettingsCollectionName).UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", userId),
            Builders<BsonDocument>.Update.Set("Disabled", disabled),
            new UpdateOptions { IsUpsert = true });
    }

    /// <summary>
    /// Permanently drops a peer from the caller's rating. Resetting is remembered rather than
    /// applied once, otherwise the peer would reappear as soon as new messages are exchanged.
    /// </summary>
    public static Task ExcludePeerAsync(IMongoDatabase database, long userId, PeerType peerType, long peerId)
    {
        var id = $"{userId}-{peerType}-{peerId}";
        return database.GetCollection<BsonDocument>(ExcludedCollectionName).UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", id),
            Builders<BsonDocument>.Update
                .Set("UserId", userId)
                .Set("PeerType", (int)peerType)
                .Set("PeerId", peerId),
            new UpdateOptions { IsUpsert = true });
    }

    /// <summary>
    /// Aggregates the caller's outgoing messages into a per-peer rating, most used first.
    /// </summary>
    public static async Task<List<PeerRating>> GetRatingsAsync(IMongoDatabase database, long userId, int now)
    {
        var excluded = await GetExcludedKeysAsync(database, userId);

        var match = new BsonDocument
        {
            { "OwnerPeerId", userId },
            { "Out", true },
            { "Date", new BsonDocument("$gt", now - RatingWindowSeconds) }
        };

        PipelineDefinition<BsonDocument, BsonDocument> pipeline = new BsonDocument[]
        {
            new("$match", match),
            new("$group", new BsonDocument
            {
                {
                    "_id", new BsonDocument
                    {
                        { "ToPeerType", "$ToPeerType" },
                        { "ToPeerId", "$ToPeerId" }
                    }
                },
                { "Count", new BsonDocument("$sum", 1) },
                { "LastDate", new BsonDocument("$max", "$Date") },
                {
                    "PhoneCalls", new BsonDocument("$sum",
                        new BsonDocument("$cond", new BsonArray
                        {
                            // Enums are persisted as their numeric value, not as their name.
                            new BsonDocument("$eq", new BsonArray { "$MessageType", (int)MessageType.PhoneCall }),
                            1,
                            0
                        }))
                },
                {
                    "Forwards", new BsonDocument("$sum",
                        new BsonDocument("$cond", new BsonArray
                        {
                            new BsonDocument("$ifNull", new BsonArray { "$FwdHeader", false }),
                            1,
                            0
                        }))
                }
            })
        };

        var grouped = await database.GetCollection<BsonDocument>(MessageCollectionName)
            .Aggregate(pipeline)
            .ToListAsync();

        var ratings = new List<PeerRating>(grouped.Count);
        foreach (var doc in grouped)
        {
            var key = doc["_id"].AsBsonDocument;
            var peerTypeValue = key.GetValue("ToPeerType", BsonNull.Value);
            if (peerTypeValue.BsonType != BsonType.Int32 && peerTypeValue.BsonType != BsonType.Int64)
            {
                continue;
            }

            var peerType = (PeerType)peerTypeValue.ToInt32();
            var peerId = GetInt64(key.GetValue("ToPeerId", BsonNull.Value));
            if (peerId == 0 || excluded.Contains((peerType, peerId)))
            {
                continue;
            }

            var count = doc["Count"].AsInt32;
            var lastDate = doc["LastDate"].ToInt64();
            var rating = count * Math.Exp(-Math.Max(0, now - lastDate) / DecaySeconds);

            ratings.Add(new PeerRating(peerType, peerId, rating,
                doc.GetValue("PhoneCalls", 0).ToInt32() > 0,
                doc.GetValue("Forwards", 0).ToInt32() > 0));
        }

        return ratings.OrderByDescending(p => p.Rating).ToList();
    }

    private static async Task<HashSet<(PeerType, long)>> GetExcludedKeysAsync(IMongoDatabase database, long userId)
    {
        var docs = await database.GetCollection<BsonDocument>(ExcludedCollectionName)
            .Find(Builders<BsonDocument>.Filter.Eq("UserId", userId))
            .ToListAsync();

        var keys = new HashSet<(PeerType, long)>();
        foreach (var doc in docs)
        {
            var peerTypeValue = doc.GetValue("PeerType", BsonNull.Value);
            if (peerTypeValue.BsonType is BsonType.Int32 or BsonType.Int64)
            {
                keys.Add(((PeerType)peerTypeValue.ToInt32(), GetInt64(doc.GetValue("PeerId", BsonNull.Value))));
            }
        }

        return keys;
    }

    private static long GetInt64(BsonValue value)
    {
        return value.BsonType switch
        {
            BsonType.Int64 => value.AsInt64,
            BsonType.Int32 => value.AsInt32,
            BsonType.Double => (long)value.AsDouble,
            _ => 0
        };
    }
}
