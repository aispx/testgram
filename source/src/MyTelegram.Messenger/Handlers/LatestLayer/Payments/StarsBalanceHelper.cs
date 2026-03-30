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
        bool stargiftAuctionBid = false, bool offer = false, string? stargiftSlug = null)
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
        });
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
        };
    }
}
