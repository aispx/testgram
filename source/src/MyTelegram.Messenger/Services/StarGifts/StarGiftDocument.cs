using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MyTelegram.Messenger.Services.StarGifts;

public class StarGiftDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    public long GiftId { get; set; }

    public long Stars { get; set; }
    public long ConvertStars { get; set; }
    public long? UpgradeStars { get; set; }
    public bool Limited { get; set; }
    public bool SoldOut { get; set; }
    public bool Birthday { get; set; }
    public bool RequirePremium { get; set; }
    public bool LimitedPerUser { get; set; }
    public int? AvailabilityTotal { get; set; }
    public int? AvailabilityRemains { get; set; }
    public long? AvailabilityResale { get; set; }
    public int? FirstSaleDate { get; set; }
    public int? LastSaleDate { get; set; }
    public long? ResellMinStars { get; set; }
    public string? Title { get; set; }
    public int? PerUserTotal { get; set; }
    public int? PerUserRemains { get; set; }
    public int? LockedUntilDate { get; set; }
    // ReleasedBy: store as peer type + id (0=user,1=channel)
    public int? ReleasedByPeerType { get; set; }
    public long? ReleasedByPeerId { get; set; }
    public long RandomId { get; set; }
    public long DocumentId { get; set; }
    public long DocumentAccessHash { get; set; }

    [BsonIgnore]
    public byte[] FileReference { get; set; } = [];

    [BsonElement("FileReference")]
    public BsonArray FileReferenceBson
    {
        get => new BsonArray(FileReference.Select(b => new BsonInt32(b)));
        set => FileReference = value?.ToArray().Select(v => (byte)v.AsInt32).ToArray() ?? [];
    }

    public int DocumentDate { get; set; }
    public string MimeType { get; set; } = "application/x-tgsticker";
    public long DocumentSize { get; set; }
    public int DcId { get; set; }

    // Auction fields
    public bool IsAuction { get; set; }
    public string? AuctionSlug { get; set; }
    public int? GiftsPerRound { get; set; }
    public int? AuctionStartDate { get; set; }
}
