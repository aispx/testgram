using MongoDB.Bson.Serialization.Attributes;

namespace MyTelegram.Messenger.Services.StarGifts;

public class SavedPaymentRequestedInfoDocument
{
    [BsonId]
    public long UserId { get; set; }

    public byte[] Info { get; set; } = [];

    public DateTime UpdatedAt { get; set; }
}

