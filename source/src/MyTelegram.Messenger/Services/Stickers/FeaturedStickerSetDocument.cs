using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Stickers;

/// <summary>One trending stickerset, in the order the server wants it shown.</summary>
[BsonIgnoreExtraElements]
public class FeaturedStickerSetDocument
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public long StickerSetId { get; set; }

    /// <summary>Ascending; ties broken by id so the list is stable across polls.</summary>
    public int Order { get; set; }

    /// <summary>
    /// Set once a stickerset drops out of the current trending list. Such sets are no longer returned by
    /// <c>messages.getFeaturedStickers</c> but remain reachable through
    /// <c>messages.getOldFeaturedStickers</c>, which is the whole purpose of that method.
    /// </summary>
    public bool Archived { get; set; }

    public static string MakeId(long stickerSetId) => $"featured-set-{stickerSetId}";
}

/// <summary>Which trending sets one user has already seen.</summary>
[BsonIgnoreExtraElements]
public class ReadFeaturedStickerSetsDocument
{
    /// <summary><c>{UserId}:{StickerSetType}</c> — normal stickers and custom emoji are read separately.</summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public long UserId { get; set; }

    public List<long> ReadSetIds { get; set; } = [];

    public static string MakeId(long userId, StickerSetType type) => $"{userId}:{type}";
}
