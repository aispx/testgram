using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MyTelegram.Messenger.Services.StarGifts;

/// <summary>
/// Per-user TON balance, stored in nanotons (1 TON = 1_000_000_000 nanotons).
/// </summary>
public class TonBalanceDocument
{
    [BsonId]
    public ObjectId Id { get; set; }
    public long UserId { get; set; }
    /// <summary>Balance in nanotons.</summary>
    public long Balance { get; set; }
}

/// <summary>
/// TON transaction history (parallel to <see cref="StarsTransactionDocument"/>).
/// Amount is stored in nanotons: positive = incoming, negative = outgoing.
/// </summary>
public class TonTransactionDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public string Id { get; set; } = string.Empty;
    public string TransactionId { get; set; } = string.Empty;
    public long UserId { get; set; }
    /// <summary>Amount in nanotons. Positive = incoming, negative = outgoing.</summary>
    public long Amount { get; set; }
    public int Date { get; set; }
    public bool Gift { get; set; }
    public bool Refund { get; set; }
    public long? PeerUserId { get; set; }
    public long? PeerChannelId { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
}
