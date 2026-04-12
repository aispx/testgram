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
        bool stargiftAuctionBid = false, bool offer = false, string? stargiftSlug = null, int? premiumGiftMonths = null,
        int? starrefCommissionPermille = null, long? starrefPeerUserId = null, long? starrefPeerChannelId = null, long? starrefAmount = null)
    {
        var transactionId = Guid.NewGuid().ToString("N");
        await db.GetCollection<StarsTransactionDocument>("star-transactions").InsertOneAsync(new StarsTransactionDocument
        {
            Id = transactionId,
            TransactionId = transactionId,
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
            StarrefCommissionPermille = starrefCommissionPermille,
            StarrefPeerUserId = starrefPeerUserId,
            StarrefPeerChannelId = starrefPeerChannelId,
            StarrefAmount = starrefAmount,
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

        // Affiliate info
        int? starrefCommissionPermille = doc.Contains("StarrefCommissionPermille") && !doc["StarrefCommissionPermille"].IsBsonNull
            ? (int?)doc["StarrefCommissionPermille"].AsInt32
            : null;
        long? starrefPeerUserId = doc.Contains("StarrefPeerUserId") && !doc["StarrefPeerUserId"].IsBsonNull
            ? doc["StarrefPeerUserId"].AsInt64
            : null;
        long? starrefPeerChannelId = doc.Contains("StarrefPeerChannelId") && !doc["StarrefPeerChannelId"].IsBsonNull
            ? doc["StarrefPeerChannelId"].AsInt64
            : null;
        long? starrefAmount = doc.Contains("StarrefAmount") && !doc["StarrefAmount"].IsBsonNull
            ? doc["StarrefAmount"].AsInt64
            : null;

        IPeer? starrefPeer = starrefPeerUserId.HasValue
            ? new TPeerUser { UserId = starrefPeerUserId.Value }
            : starrefPeerChannelId.HasValue
                ? new TPeerChannel { ChannelId = starrefPeerChannelId.Value }
                : null;

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
            StarrefCommissionPermille = starrefCommissionPermille,
            StarrefPeer = starrefPeer,
            StarrefAmount = starrefAmount.HasValue ? new TStarsAmount { Amount = starrefAmount.Value } : null,
        };
    }

    public static TStarsTransaction ToTl(StarsTransactionDocument doc)
    {
        IStarsTransactionPeer peer = doc.PeerUserId.HasValue
            ? new TStarsTransactionPeer { Peer = new TPeerUser { UserId = doc.PeerUserId.Value } }
            : doc.PeerChannelId.HasValue
                ? new TStarsTransactionPeer { Peer = new TPeerChannel { ChannelId = doc.PeerChannelId.Value } }
                : new TStarsTransactionPeerUnsupported();

        IPeer? starrefPeer = doc.StarrefPeerUserId.HasValue
            ? new TPeerUser { UserId = doc.StarrefPeerUserId.Value }
            : doc.StarrefPeerChannelId.HasValue
                ? new TPeerChannel { ChannelId = doc.StarrefPeerChannelId.Value }
                : null;

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
            StarrefCommissionPermille = doc.StarrefCommissionPermille,
            StarrefPeer = starrefPeer,
            StarrefAmount = doc.StarrefAmount.HasValue ? new TStarsAmount { Amount = doc.StarrefAmount.Value } : null,
        };
    }
}
