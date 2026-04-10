using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

public static class StarsBalanceHelper
{
    public static async Task<long> GetBalanceAsync(IMongoDatabase db, long userId)
    {
        var doc = await db.GetCollection<StarsBalanceDocument>("star-balances")
            .Find(x => x.UserId == userId).FirstOrDefaultAsync();
        return doc?.Balance ?? 0;
    }

    public static async Task<long> AddBalanceAsync(IMongoDatabase db, long userId, long delta)
    {
        var col = db.GetCollection<StarsBalanceDocument>("star-balances");
        var result = await col.FindOneAndUpdateAsync(
            x => x.UserId == userId,
            Builders<StarsBalanceDocument>.Update.Inc(x => x.Balance, delta),
            new FindOneAndUpdateOptions<StarsBalanceDocument> { ReturnDocument = ReturnDocument.After, IsUpsert = true }
        );
        return result?.Balance ?? delta;
    }

    public static async Task AddTransactionAsync(IMongoDatabase db, long userId, long amount, bool gift = false,
        long? peerUserId = null, long? peerChannelId = null, string? title = null, bool stargiftUpgrade = false,
        bool stargiftAuctionBid = false, bool offer = false, string? stargiftSlug = null, int? premiumGiftMonths = null)
    {
        await db.GetCollection<StarsTransactionDocument>("star-transactions").InsertOneAsync(new StarsTransactionDocument
        {
            TransactionId = Guid.NewGuid().ToString("N"),
            UserId = userId,
            Amount = amount,
            Date = DateTime.UtcNow.ToTimestamp(),
            Gift = gift,
            PeerUserId = peerUserId,
            PeerChannelId = peerChannelId,
            Title = title,
            StargiftUpgrade = stargiftUpgrade,
            StargiftAuctionBid = stargiftAuctionBid,
            Offer = offer,
            StargiftSlug = stargiftSlug,
            PremiumGiftMonths = premiumGiftMonths,
        });
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
        var stargiftUpgrade = doc.Contains("StargiftUpgrade") && doc["StargiftUpgrade"].AsBoolean;
        var stargiftAuctionBid = doc.Contains("StargiftAuctionBid") && doc["StargiftAuctionBid"].AsBoolean;
        var offer = doc.Contains("Offer") && doc["Offer"].AsBoolean;
        var premiumGiftMonths = doc.Contains("PremiumGiftMonths") && !doc["PremiumGiftMonths"].IsBsonNull
            ? (int?)doc["PremiumGiftMonths"].AsInt32
            : null;

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
            Amount = new TStarsAmount { Amount = amount },
            Date = date,
            Peer = peer,
            Gift = gift,
            Refund = refund,
            Title = title,
            StargiftUpgrade = stargiftUpgrade,
            StargiftAuctionBid = stargiftAuctionBid,
            Offer = offer,
            PremiumGiftMonths = premiumGiftMonths,
        };
    }

    public static TStarsTransaction ToTl(StarsTransactionDocument doc)
    {
        IStarsTransactionPeer peer = doc.PeerUserId.HasValue
            ? new TStarsTransactionPeer { Peer = new TPeerUser { UserId = doc.PeerUserId.Value } }
            : doc.PeerChannelId.HasValue
                ? new TStarsTransactionPeer { Peer = new TPeerChannel { ChannelId = doc.PeerChannelId.Value } }
                : new TStarsTransactionPeerUnsupported();

        return new TStarsTransaction
        {
            Id = doc.TransactionId,
            Amount = new TStarsAmount { Amount = doc.Amount }, // signed: negative = outgoing
            Date = doc.Date,
            Peer = peer,
            Gift = doc.Gift,
            Refund = doc.Refund,
            Title = doc.Title,
            StargiftUpgrade = doc.StargiftUpgrade,
            StargiftAuctionBid = doc.StargiftAuctionBid,
            Offer = doc.Offer,
            PremiumGiftMonths = doc.PremiumGiftMonths,
        };
    }
}
