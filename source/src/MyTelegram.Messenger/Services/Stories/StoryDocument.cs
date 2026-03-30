using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MyTelegram.Messenger.Services.Stories;

public class StoryDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    public long OwnerPeerId { get; set; }
    public int OwnerPeerType { get; set; }
    public int StoryId { get; set; }
    public long Date { get; set; }
    public long ExpireDate { get; set; }
    public string? Caption { get; set; }
    public int MediaType { get; set; }
    public long MediaFileId { get; set; }
    public long MediaAccessHash { get; set; }
    public byte[] MediaFileReference { get; set; } = [];
    public int MediaDcId { get; set; }
    public long MediaSize { get; set; }
    public string? MediaMimeType { get; set; }
    public int ViewsCount { get; set; }
    public int ForwardsCount { get; set; }
    public int ReactionsCount { get; set; }
    public bool Pinned { get; set; }
    public bool NoForwards { get; set; }
    public bool Deleted { get; set; }
    public bool Archived { get; set; }
    public bool IsLive { get; set; }
    public bool CloseFriends { get; set; }
    public bool Reported { get; set; }
    public bool Edited { get; set; }
    public bool RtmpStream { get; set; }
    public bool MessagesEnabled { get; set; }
    public long SendPaidMessagesStars { get; set; }
    public long GroupCallId { get; set; }
    public long GroupCallAccessHash { get; set; }
    public string? RtmpUrl { get; set; }
    public string? RtmpStreamKey { get; set; }
    public long RandomId { get; set; }
    public int? Period { get; set; }
    public long FwdFromPeerId { get; set; }
    public int? FwdFromStoryId { get; set; }
    public int? AlbumId { get; set; }
    public string? AlbumTitle { get; set; }

    public List<StoryPrivacyRule> PrivacyRules { get; set; } = [];
    public string? Entities { get; set; }
    public int? VideoWidth { get; set; }
    public int? VideoHeight { get; set; }
    public int? VideoDuration { get; set; }
    public byte[]? VideoThumbBytes { get; set; }
}

public class StoryPrivacyRule
{
    public int Type { get; set; }
    public long? UserId { get; set; }
    public long? ChatId { get; set; }
}
