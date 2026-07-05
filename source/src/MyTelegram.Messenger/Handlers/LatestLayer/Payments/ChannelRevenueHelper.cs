using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

/// <summary>
/// Channel/bot revenue ledger. Funds for paid messages, paid reactions, paid media and
/// suggested-post settlements are credited HERE (not to the owner user's wallet).
/// The owner can later withdraw via <c>payments.getStarsRevenueWithdrawalUrl</c>.
/// </summary>
public static class ChannelRevenueHelper
{
    public const string BalanceCollection = "channel-revenue-balances";
    public const string TransactionCollection = "channel-revenue-transactions";

    public const string StarsCurrency = "stars";
    public const string TonCurrency = "ton";

    public static async Task<(long Current, long Overall)> GetBalanceAsync(IMongoDatabase db, long peerId, string currency)
    {
        var doc = await db.GetCollection<ChannelRevenueDocument>(BalanceCollection)
            .Find(x => x.PeerId == peerId && x.Currency == currency)
            .FirstOrDefaultAsync();
        return (doc?.CurrentBalance ?? 0, doc?.OverallRevenue ?? 0);
    }

    public static async Task<ChannelRevenueDocument> CreditAsync(IMongoDatabase db, long peerId, string currency, long amount,
        long? sourceUserId = null, long? sourceChannelId = null, string? title = null, string? description = null,
        int? msgId = null, TVector<IMessageMedia>? extendedMedia = null)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Credit must be positive.");

        var col = db.GetCollection<ChannelRevenueDocument>(BalanceCollection);
        var filter = Builders<ChannelRevenueDocument>.Filter.Eq(x => x.PeerId, peerId) &
                     Builders<ChannelRevenueDocument>.Filter.Eq(x => x.Currency, currency);
        var update = Builders<ChannelRevenueDocument>.Update
            .Inc(x => x.CurrentBalance, amount)
            .Inc(x => x.OverallRevenue, amount);
        var result = await col.FindOneAndUpdateAsync(filter, update, new FindOneAndUpdateOptions<ChannelRevenueDocument>
        {
            ReturnDocument = ReturnDocument.After,
            IsUpsert = true
        });

        await LogTransactionAsync(db, peerId, currency, amount, sourceUserId, sourceChannelId, refund: false, title, description, msgId, extendedMedia);
        return result;
    }

    /// <summary>
    /// Debit the channel's current balance (e.g. for withdrawal). Throws if not enough funds.
    /// OverallRevenue is preserved.
    /// </summary>
    public static async Task<ChannelRevenueDocument> DebitAsync(IMongoDatabase db, long peerId, string currency, long amount,
        long? targetUserId = null, string? title = null, string? description = null, bool refund = false)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Debit must be positive.");

        var col = db.GetCollection<ChannelRevenueDocument>(BalanceCollection);
        var filter = Builders<ChannelRevenueDocument>.Filter.Eq(x => x.PeerId, peerId) &
                     Builders<ChannelRevenueDocument>.Filter.Eq(x => x.Currency, currency) &
                     Builders<ChannelRevenueDocument>.Filter.Gte(x => x.CurrentBalance, amount);
        var update = Builders<ChannelRevenueDocument>.Update.Inc(x => x.CurrentBalance, -amount);
        var result = await col.FindOneAndUpdateAsync(filter, update, new FindOneAndUpdateOptions<ChannelRevenueDocument>
        {
            ReturnDocument = ReturnDocument.After
        });

        if (result == null)
        {
            throw new InvalidOperationException($"Insufficient {currency} balance on peer {peerId}.");
        }

        await LogTransactionAsync(db, peerId, currency, -amount, sourceUserId: targetUserId, sourceChannelId: null, refund, title, description);
        return result;
    }

    private static async Task LogTransactionAsync(IMongoDatabase db, long peerId, string currency, long amount,
        long? sourceUserId, long? sourceChannelId, bool refund, string? title, string? description,
        int? msgId = null, TVector<IMessageMedia>? extendedMedia = null)
    {
        var transactionId = Guid.NewGuid().ToString("N");
        await db.GetCollection<ChannelRevenueTransactionDocument>(TransactionCollection).InsertOneAsync(new ChannelRevenueTransactionDocument
        {
            Id = transactionId,
            TransactionId = transactionId,
            PeerId = peerId,
            Currency = currency,
            Amount = amount,
            Date = DateTime.UtcNow.ToTimestamp(),
            Refund = refund,
            SourceUserId = sourceUserId,
            SourceChannelId = sourceChannelId,
            Title = title,
            Description = description,
            MsgId = msgId,
            ExtendedMedia = extendedMedia?.Select(x => x.ToBytes()).ToList(),
        });
    }

    public static IStarsAmount BuildAmount(string currency, long amount)
        => currency == TonCurrency
            ? new TStarsTonAmount { Amount = amount }
            : new TStarsAmount { Amount = amount };

    public static TStarsTransaction ToTl(ChannelRevenueTransactionDocument doc)
    {
        IStarsTransactionPeer peer = doc.SourceUserId.HasValue
            ? new TStarsTransactionPeer { Peer = new TPeerUser { UserId = doc.SourceUserId.Value } }
            : doc.SourceChannelId.HasValue
                ? new TStarsTransactionPeer { Peer = new TPeerChannel { ChannelId = doc.SourceChannelId.Value } }
                : new TStarsTransactionPeerUnsupported();

        return new TStarsTransaction
        {
            Id = doc.TransactionId,
            Amount = BuildAmount(doc.Currency, doc.Amount),
            Date = doc.Date,
            Peer = peer,
            Refund = doc.Refund,
            Title = doc.Title,
            Description = doc.Description,
            MsgId = doc.MsgId,
            ExtendedMedia = doc.ExtendedMedia?.Count > 0
                ? new TVector<IMessageMedia>(doc.ExtendedMedia.Select(x =>
                {
                    var memory = new ReadOnlyMemory<byte>(x);
                    return memory.Read<IMessageMedia>();
                }).ToList())
                : null,
        };
    }
}
