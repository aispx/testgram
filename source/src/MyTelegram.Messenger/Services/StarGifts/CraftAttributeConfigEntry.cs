using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MyTelegram.Messenger.Services.StarGifts;

/// <summary>
/// Stored in MongoDB collection "star-gift-craft-config"
/// Defines attributes available for crafted gifts with rarity tiers
/// </summary>
public class CraftAttributeConfigEntry
{
    [BsonElement("type")]
    public string Type { get; set; } = ""; // "model", "pattern", "backdrop"

    [BsonElement("gift_id")]
    public long GiftId { get; set; } // 0 = applies to all gifts

    [BsonElement("name")]
    public string Name { get; set; } = "";

    [BsonElement("rarity_tier")]
    public string RarityTier { get; set; } = ""; // "uncommon", "rare", "epic", "legendary"

    [BsonElement("rarity_permille")]
    public int RarityPermille { get; set; } // Weight for random selection

    // For model/pattern attributes
    [BsonElement("document_id")]
    public long? DocumentId { get; set; }

    [BsonElement("document_access_hash")]
    public long? DocumentAccessHash { get; set; }

    [BsonIgnore]
    public byte[]? FileReference { get; set; }

    [BsonElement("file_reference")]
    [BsonIgnoreIfNull]
    public BsonArray? FileReferenceBson
    {
        get => FileReference == null ? null : new BsonArray(FileReference.Select(b => new BsonInt32(b)));
        set => FileReference = value?.Select(v => (byte)v.AsInt32).ToArray();
    }

    [BsonElement("document_date")]
    public int? DocumentDate { get; set; }

    [BsonElement("mime_type")]
    public string? MimeType { get; set; }

    [BsonElement("document_size")]
    public long? DocumentSize { get; set; }

    [BsonElement("dc_id")]
    public int? DcId { get; set; }

    // For backdrop attributes
    [BsonElement("backdrop_id")]
    public int? BackdropId { get; set; }

    [BsonElement("center_color")]
    public int? CenterColor { get; set; }

    [BsonElement("edge_color")]
    public int? EdgeColor { get; set; }

    [BsonElement("pattern_color")]
    public int? PatternColor { get; set; }

    [BsonElement("text_color")]
    public int? TextColor { get; set; }
}
