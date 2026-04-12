using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MyTelegram.Messenger.Services.StarGifts;

public class StarsBalanceDocument
{
    [BsonId]
    public ObjectId Id { get; set; }
    public long UserId { get; set; }
    public long Balance { get; set; }
}

public class StarsTransactionDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public string Id { get; set; } = string.Empty;
    public string TransactionId { get; set; } = string.Empty;
    public long UserId { get; set; }
    public long Amount { get; set; }   // positive = incoming, negative = outgoing
    public int Date { get; set; }
    public bool Gift { get; set; }
    public bool Refund { get; set; }
    // peer info
    public long? PeerUserId { get; set; }
    public long? PeerChannelId { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public bool StargiftUpgrade { get; set; }
    public bool StargiftAuctionBid { get; set; }
    public bool Offer { get; set; }
    public string? StargiftSlug { get; set; }
    public int? PremiumGiftMonths { get; set; }
    // affiliate info
    public int? StarrefCommissionPermille { get; set; }
    public long? StarrefPeerUserId { get; set; }
    public long? StarrefPeerChannelId { get; set; }
    public long? StarrefAmount { get; set; }
}
