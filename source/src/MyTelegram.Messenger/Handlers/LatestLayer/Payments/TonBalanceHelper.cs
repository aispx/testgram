using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

/// <summary>
/// TON balance/transaction storage. All amounts are in nanotons (1 TON = 1_000_000_000 nanotons),
/// matching <see cref="TStarsTonAmount"/> semantics.
/// </summary>
public static class TonBalanceHelper
{
    public const string BalanceCollection = "ton-balances";
    public const string TransactionCollection = "ton-transactions";

    public static async Task<long> GetBalanceAsync(IMongoDatabase db, long userId)
    {
        var doc = await db.GetCollection<TonBalanceDocument>(BalanceCollection)
            .Find(x => x.UserId == userId).FirstOrDefaultAsync();
        return doc?.Balance ?? 0;
    }

    public static async Task<long> AddBalanceAsync(IMongoDatabase db, long userId, long deltaNanos)
    {
        var col = db.GetCollection<TonBalanceDocument>(BalanceCollection);
        var result = await col.FindOneAndUpdateAsync(
            x => x.UserId == userId,
            Builders<TonBalanceDocument>.Update.Inc(x => x.Balance, deltaNanos),
            new FindOneAndUpdateOptions<TonBalanceDocument> { ReturnDocument = ReturnDocument.After, IsUpsert = true }
        );
        return result?.Balance ?? deltaNanos;
    }

    public static async Task AddTransactionAsync(IMongoDatabase db,
        long userId,
        long amountNanos,
        bool gift = false,
        bool refund = false,
        long? peerUserId = null,
        long? peerChannelId = null,
        string? title = null,
        string? description = null)
    {
        var transactionId = Guid.NewGuid().ToString("N");
        await db.GetCollection<TonTransactionDocument>(TransactionCollection).InsertOneAsync(new TonTransactionDocument
        {
            Id = transactionId,
            TransactionId = transactionId,
            UserId = userId,
            Amount = amountNanos,
            Date = DateTime.UtcNow.ToTimestamp(),
            Gift = gift,
            Refund = refund,
            PeerUserId = peerUserId,
            PeerChannelId = peerChannelId,
            Title = title,
            Description = description,
        });
    }

    public static TStarsTransaction ToTl(TonTransactionDocument doc)
    {
        IStarsTransactionPeer peer = doc.PeerUserId.HasValue
            ? new TStarsTransactionPeer { Peer = new TPeerUser { UserId = doc.PeerUserId.Value } }
            : doc.PeerChannelId.HasValue
                ? new TStarsTransactionPeer { Peer = new TPeerChannel { ChannelId = doc.PeerChannelId.Value } }
                : new TStarsTransactionPeerUnsupported();

        return new TStarsTransaction
        {
            Id = doc.TransactionId,
            Amount = new TStarsTonAmount { Amount = doc.Amount },
            Date = doc.Date,
            Peer = peer,
            Gift = doc.Gift,
            Refund = doc.Refund,
            Title = doc.Title,
            Description = doc.Description,
        };
    }

    public static TStarsTransaction BsonToTl(BsonDocument doc)
    {
        var transactionId = doc.Contains("TransactionId") && !doc["TransactionId"].IsBsonNull
            ? doc["TransactionId"].AsString
            : doc["_id"].AsString;
        var amount = doc.Contains("Amount") ? doc["Amount"].AsInt64 : 0L;
        var date = doc.Contains("Date") ? doc["Date"].AsInt32 : 0;
        var gift = doc.Contains("Gift") && doc["Gift"].AsBoolean;
        var refund = doc.Contains("Refund") && doc["Refund"].AsBoolean;
        var title = doc.Contains("Title") && !doc["Title"].IsBsonNull ? doc["Title"].AsString : null;
        var description = doc.Contains("Description") && !doc["Description"].IsBsonNull ? doc["Description"].AsString : null;

        long? peerUserId = doc.Contains("PeerUserId") && !doc["PeerUserId"].IsBsonNull
            ? doc["PeerUserId"].AsInt64
            : null;
        long? peerChannelId = doc.Contains("PeerChannelId") && !doc["PeerChannelId"].IsBsonNull
            ? doc["PeerChannelId"].AsInt64
            : null;

        IStarsTransactionPeer peer = peerUserId.HasValue
            ? new TStarsTransactionPeer { Peer = new TPeerUser { UserId = peerUserId.Value } }
            : peerChannelId.HasValue
                ? new TStarsTransactionPeer { Peer = new TPeerChannel { ChannelId = peerChannelId.Value } }
                : new TStarsTransactionPeerUnsupported();

        return new TStarsTransaction
        {
            Id = transactionId,
            Amount = new TStarsTonAmount { Amount = amount },
            Date = date,
            Peer = peer,
            Gift = gift,
            Refund = refund,
            Title = title,
            Description = description,
        };
    }
}
