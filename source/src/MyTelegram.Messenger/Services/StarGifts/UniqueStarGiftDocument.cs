using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MyTelegram.Messenger.Services.StarGifts;

/// <summary>
/// Stored in MongoDB collection "unique-star-gifts"
/// </summary>
public class UniqueStarGiftDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    public long UniqueId { get; set; }       // unique collectible id (auto-increment per gift_id)
    public long GiftId { get; set; }         // original star-gift id
    public string Title { get; set; } = "";
    public string Slug { get; set; } = "";
    public int Num { get; set; }             // number among collectibles of same type
    public long OwnerUserId { get; set; }
    public long OwnerChannelId { get; set; }
    public long FromUserId { get; set; }
    public int Date { get; set; }
    public int AvailabilityIssued { get; set; }
    public int AvailabilityTotal { get; set; }
    public bool NameHidden { get; set; }
    public string? MessageText { get; set; }
    public long ResellStars { get; set; }  // 0 = not for sale
    public long InitialSaleStars { get; set; } // original purchase price
    public long OriginalRecipientUserId { get; set; } // original recipient (never changes on transfer)

    // Attributes stored as JSON
    public UniqueGiftAttribute[] Attributes { get; set; } = [];

    // Sticker (model attribute document)
    public long DocumentId { get; set; }
    public long DocumentAccessHash { get; set; }
    public byte[] FileReference { get; set; } = [];
    public int DocumentDate { get; set; }
    public string MimeType { get; set; } = "application/x-tgsticker";
    public long DocumentSize { get; set; }
    public int DcId { get; set; }
    
    // Offer minimum stars for Layer 220 offers feature
    public int OfferMinStars { get; set; }
}

public class UniqueGiftAttribute
{
    public string Type { get; set; } = ""; // "model", "pattern", "backdrop"
    public string Name { get; set; } = "";
    public int RarityPermille { get; set; }

    // model/pattern
    public long? DocumentId { get; set; }
    public long? DocumentAccessHash { get; set; }
    public byte[]? FileReference { get; set; }
    public int? DocumentDate { get; set; }
    public string? MimeType { get; set; }
    public long? DocumentSize { get; set; }
    public int? DcId { get; set; }

    // backdrop
    public int? BackdropId { get; set; }
    public int? CenterColor { get; set; }
    public int? EdgeColor { get; set; }
    public int? PatternColor { get; set; }
    public int? TextColor { get; set; }
}
