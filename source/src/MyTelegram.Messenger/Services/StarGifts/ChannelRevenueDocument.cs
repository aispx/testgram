using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MyTelegram.Messenger.Services.StarGifts;

/// <summary>
/// Per-channel/bot revenue balance, separated by currency (stars or ton/nanotons).
/// CurrentBalance is the amount currently available, OverallRevenue is the sum of all credits ever
/// (never decreases — used by payments.starsRevenueStats overall_revenue).
/// </summary>
public class ChannelRevenueDocument
{
    [BsonId]
    public ObjectId Id { get; set; }
    /// <summary>Channel id (positive) or bot user id, owner of the balance.</summary>
    public long PeerId { get; set; }
    /// <summary>"stars" or "ton".</summary>
    public string Currency { get; set; } = "stars";
    /// <summary>Current available balance (in stars or nanotons).</summary>
    public long CurrentBalance { get; set; }
    /// <summary>Total revenue ever received (never decreases by withdrawal).</summary>
    public long OverallRevenue { get; set; }
}

public class ChannelRevenueTransactionDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public string Id { get; set; } = string.Empty;
    public string TransactionId { get; set; } = string.Empty;
    public long PeerId { get; set; }
    public string Currency { get; set; } = "stars";
    /// <summary>Positive = incoming, negative = outgoing (withdrawal/refund).</summary>
    public long Amount { get; set; }
    public int Date { get; set; }
    public bool Refund { get; set; }
    public long? SourceUserId { get; set; }
    public long? SourceChannelId { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
}
