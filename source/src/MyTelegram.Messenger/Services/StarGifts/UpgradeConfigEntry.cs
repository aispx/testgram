using MongoDB.Bson.Serialization.Attributes;

namespace MyTelegram.Messenger.Services.StarGifts;

public class UpgradeConfigEntry
{
    [BsonElement("type")]
    public string Type { get; set; } = "";
    [BsonElement("gift_id")]
    public long GiftId { get; set; }
    [BsonElement("name")]
    public string Name { get; set; } = "";
    [BsonElement("rarity_permille")]
    public int RarityPermille { get; set; }

    [BsonElement("document_id")]
    public long? DocumentId { get; set; }
    [BsonElement("document_access_hash")]
    public long? DocumentAccessHash { get; set; }
    [BsonElement("file_reference")]
    public byte[]? FileReference { get; set; }
    [BsonElement("document_date")]
    public int? DocumentDate { get; set; }
    [BsonElement("mime_type")]
    public string? MimeType { get; set; }
    [BsonElement("document_size")]
    public long? DocumentSize { get; set; }
    [BsonElement("dc_id")]
    public int? DcId { get; set; }

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
