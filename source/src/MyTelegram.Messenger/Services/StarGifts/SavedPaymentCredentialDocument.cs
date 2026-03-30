using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MyTelegram.Messenger.Services.StarGifts;

public class SavedPaymentCredentialDocument
{
    [BsonId]
    public ObjectId Id { get; set; }
    public long UserId { get; set; }
    public string PaymentMethodId { get; set; } = "";  // Stripe payment_method id (pm_xxx)
    public string Title { get; set; } = "";             // e.g. "Visa •••• 4242"
    public DateTime CreatedAt { get; set; }
}
