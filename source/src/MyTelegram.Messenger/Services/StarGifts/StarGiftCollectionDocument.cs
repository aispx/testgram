using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MyTelegram.Messenger.Services.StarGifts;

public class StarGiftCollectionDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    public int CollectionId { get; set; }
    public long OwnerUserId { get; set; }
    public long OwnerChannelId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public int Date { get; set; }

    // Gift references: list of (MsgId or SavedId or Slug) in order
    public List<StarGiftCollectionItem> Items { get; set; } = [];
}

public class StarGiftCollectionItem
{
    public int? MsgId { get; set; }       // inputSavedStarGiftUser
    public long? SavedId { get; set; }    // inputSavedStarGiftChat
    public string? Slug { get; set; }     // inputSavedStarGiftSlug
}
