using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MyTelegram.Messenger.Services.StarGifts;

// IMPORTANT: BSON element names mirror the seeded documents in
// `star-gift-upgrade-config` (see scripts/init-star-gifts.sh) which use
// PascalCase. Do NOT reintroduce snake_case attributes here — the previous
// snake_case mapping silently broke deserialization, so every upgrade fell
// through to the fallback branch in UpgradeAttributeHelper and produced
// model=<gift's own sticker>, pattern=<gift's own sticker>, backdrop=Default.
public class UpgradeConfigEntry
{
    public string Type { get; set; } = "";
    public long GiftId { get; set; }
    public string Name { get; set; } = "";
    public int RarityPermille { get; set; }

    public long? DocumentId { get; set; }
    public long? DocumentAccessHash { get; set; }

    [BsonIgnore]
    public byte[]? FileReference { get; set; }

    // Some seeded documents store FileReference as a BSON array of int32
    // values (see seed_reactions.py / add-klitor-gift.py).  The default
    // driver mapping refuses to convert int32→byte, so we adapt manually.
    [BsonElement("FileReference")]
    [BsonIgnoreIfNull]
    public BsonArray? FileReferenceBson
    {
        get => FileReference == null ? null : new BsonArray(FileReference.Select(b => new BsonInt32(b)));
        set => FileReference = value?.Select(v => (byte)v.AsInt32).ToArray();
    }

    public int? DocumentDate { get; set; }
    public string? MimeType { get; set; }
    public long? DocumentSize { get; set; }
    public int? DcId { get; set; }

    public int? BackdropId { get; set; }
    public int? CenterColor { get; set; }
    public int? EdgeColor { get; set; }
    public int? PatternColor { get; set; }
    public int? TextColor { get; set; }
}
