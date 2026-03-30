using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MyTelegram.Messenger.Services.StarGifts;

/// <summary>
/// Stored in MongoDB collection "saved-star-gifts"
/// </summary>
public class SavedStarGiftDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    public long OwnerUserId { get; set; }   // recipient (user)
    public long OwnerChannelId { get; set; } // recipient (channel)
    public long FromUserId { get; set; }    // sender
    public int MessageId { get; set; }      // service message id in recipient's chat
    public long GiftId { get; set; }
    public long Stars { get; set; }
    public long ConvertStars { get; set; }
    public long? UpgradeStars { get; set; }
    public bool NameHidden { get; set; }
    public bool Saved { get; set; }         // displayed on profile
    public int Date { get; set; }
    public string? MessageText { get; set; }
    public long RandomId { get; set; }
    public bool PinnedToTop { get; set; }
    public bool? ChatNotificationsEnabled { get; set; } // channel-only
    public bool IsUnique { get; set; }   // upgraded to collectible
    public string? UniqueSlug { get; set; }
    public bool IsAuction { get; set; }
    public int? GiftNum { get; set; }
    public bool PrepaidUpgrade { get; set; }
    public string? PrepaidUpgradeHash { get; set; }
    public int? CanResellAt { get; set; }  // won from auction — cannot be resold

    // Sticker fields
    public long DocumentId { get; set; }
    public long DocumentAccessHash { get; set; }
    public byte[] FileReference { get; set; } = [];
    public int DocumentDate { get; set; }
    public string MimeType { get; set; } = "application/x-tgsticker";
    public long DocumentSize { get; set; }
    public int DcId { get; set; }
}
