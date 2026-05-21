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
    public bool OriginalDetailsDropped { get; set; }
    public string? MessageText { get; set; }
    public TVector<IMessageEntity>? MessageEntities { get; set; }
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

    // Transfer restrictions (from telelakel findings)
    public int? TransferLockedUntil { get; set; }  // Unix timestamp - can't transfer before this date
    public bool WasOnBlockchain { get; set; }      // If true, can't be used in first crafting slot
    public bool IsFromBlockchain { get; set; }     // Bot API 9.2+ - blockchain-assigned gift

    // Layer 223+ craft fields
    public bool Burned { get; set; }               // Gift was burned in crafting
    public bool Crafted { get; set; }              // Gift was created via crafting
    public int? CraftChancePermille { get; set; }  // Craft success chance (0-1000)

    // Bot API 9.1+ resale tracking
    public long? LastResaleStars { get; set; }     // Last resale price

    // Layer 206: TON resale pricing (in nanotons, 1 TON = 1e9 nanotons).
    // 0 = not listed for sale in TON.
    public long ResellTon { get; set; }

    // Layer 206: when true, this unique gift can only be purchased with TON —
    // stars-priced offers are rejected by UpdateStarGiftPrice / SendStarsForm.
    public bool ResaleTonOnly { get; set; }

    // Layer 206 resale tracking in TON.
    public long? LastResaleTon { get; set; }

    // Emoji status expiration (unix timestamp), set when gift is used as emoji status
    public int? Until { get; set; }
}

public class UniqueGiftAttribute
{
    public string Type { get; set; } = ""; // "model", "pattern", "backdrop"
    public string Name { get; set; } = "";
    public int RarityPermille { get; set; }
    public bool Crafted { get; set; } // Set to true for crafted gifts (Layer 222+)
    public string? RarityTier { get; set; } // "uncommon", "rare", "epic", "legendary"

    // model/pattern
    public long? DocumentId { get; set; }
    public long? DocumentAccessHash { get; set; }
    public byte[]? FileReference { get; set; }
    public int? DocumentDate { get; set; }
    public string? MimeType { get; set; }
    public long? DocumentSize { get; set; }
    public int? DcId { get; set; }

    // collectible
    public long? CollectibleId { get; set; }

    // backdrop
    public int? BackdropId { get; set; }
    public int? CenterColor { get; set; }
    public int? EdgeColor { get; set; }
    public int? PatternColor { get; set; }
    public int? TextColor { get; set; }
}
