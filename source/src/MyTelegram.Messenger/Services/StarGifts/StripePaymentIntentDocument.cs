using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MyTelegram.Messenger.Services.StarGifts;

public class StripePaymentIntentDocument
{
    [BsonId]
    public ObjectId Id { get; set; }
    public long FormId { get; set; }
    public string PaymentIntentId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public long UserId { get; set; }
    public long RecipientUserId { get; set; }  // 0 = topup for self, >0 = gift to another user
    public long Stars { get; set; }
    public DateTime CreatedAt { get; set; }
    public byte[]? GiveawayPurpose { get; set; }  // Serialized TInputStorePaymentStarsGiveaway or TInputStorePaymentPremiumGiveaway
}
