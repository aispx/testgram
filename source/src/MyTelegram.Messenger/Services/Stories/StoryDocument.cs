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

    /// <summary>
    /// Pinned to the top of the profile, see stories.togglePinnedToTop. At most
    /// <c>stories_pinned_to_top_count_max</c> stories per peer may have this set.
    /// </summary>
    public bool PinnedToTop { get; set; }

    public bool NoForwards { get; set; }
    public bool Deleted { get; set; }

    /// <summary>
    /// Every story goes to the archive on creation (Telegram semantics); <see cref="Pinned"/>
    /// additionally keeps it on the profile after expiry.
    /// </summary>
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
    public int FwdFromPeerType { get; set; }
    public int? FwdFromStoryId { get; set; }

    /// <summary>Set when the media was modified before reposting (<c>fwd_modified</c>).</summary>
    public bool FwdModified { get; set; }

    /// <summary>
    /// Albums the story belongs to. A story may be in several albums, matching
    /// <c>storyItem.albums: Vector&lt;int&gt;</c>. Album titles live in <see cref="StoryAlbumDocument"/>.
    /// </summary>
    public List<int> AlbumIds { get; set; } = [];

    /// <summary>Normalized (lowercase, no leading '#') hashtags parsed out of the caption.</summary>
    public List<string> Hashtags { get; set; } = [];

    /// <summary>Search tokens for <see cref="Hashtags"/>, produced by <c>ITokenizer</c>.</summary>
    public List<long> HashtagTokens { get; set; } = [];

    public long? MusicDocumentId { get; set; }
    public long? MusicAccessHash { get; set; }

    public List<StoryPrivacyRule> PrivacyRules { get; set; } = [];
    public List<StoryMediaArea> MediaAreas { get; set; } = [];
    public string? Entities { get; set; }
    public int? VideoWidth { get; set; }
    public int? VideoHeight { get; set; }
    public int? VideoDuration { get; set; }

    /// <summary>
    /// Inline low-resolution preview (<c>photoStrippedSize</c>) for the story's media, whether photo
    /// or video. Kept under its original Bson name so existing documents keep deserializing.
    /// </summary>
    /// <remarks>
    /// The client draws the profile preview tile from this. Without it the tile stays blank until the
    /// full file loads, so the server generates one at upload time.
    /// </remarks>
    [BsonElement("VideoThumbBytes")]
    public byte[]? StrippedThumbBytes { get; set; }
}

public class StoryPrivacyRule
{
    /// <summary>See <see cref="StoryPrivacyRuleType"/>.</summary>
    public int Type { get; set; }

    /// <summary>Users the rule applies to, for the allow/disallow-users rules.</summary>
    public List<long> UserIds { get; set; } = [];

    /// <summary>Chats the rule applies to, for the allow/disallow-chat-participants rules.</summary>
    public List<long> ChatIds { get; set; } = [];
}

/// <summary>
/// Stored discriminator for <see cref="StoryPrivacyRule.Type"/>. Values are persisted in MongoDB and
/// must stay stable; 0-6 match what earlier builds wrote.
/// </summary>
public static class StoryPrivacyRuleType
{
    public const int AllowAll = 0;
    public const int AllowContacts = 1;
    public const int DisallowAll = 2;
    public const int DisallowContacts = 3;
    public const int AllowCloseFriends = 4;
    public const int DisallowUsers = 5;
    public const int AllowUsers = 6;
    public const int AllowChatParticipants = 7;
    public const int DisallowChatParticipants = 8;
    public const int AllowPremium = 9;
    public const int AllowBots = 10;
    public const int DisallowBots = 11;
}

/// <summary>
/// A <a href="https://corefork.telegram.org/api/stories#media-areas">media area</a> attached to a story.
/// One flat document covers every area constructor; <see cref="Type"/> selects which fields are set.
/// </summary>
public class StoryMediaArea
{
    /// <summary>See <see cref="StoryMediaAreaType"/>.</summary>
    public int Type { get; set; }

    // mediaAreaCoordinates
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public double Rotation { get; set; }
    public double? Radius { get; set; }

    // geo point / venue
    public double? GeoLat { get; set; }
    public double? GeoLong { get; set; }
    public long? GeoAccessHash { get; set; }
    public int? GeoAccuracyRadius { get; set; }

    // geoPointAddress, on mediaAreaGeoPoint
    public string? GeoCountryIso2 { get; set; }
    public string? GeoState { get; set; }
    public string? GeoCity { get; set; }
    public string? GeoStreet { get; set; }

    public string? Address { get; set; }
    public string? VenueId { get; set; }
    public string? VenueType { get; set; }
    public string? Provider { get; set; }
    public string? Title { get; set; }
    public long? QueryId { get; set; }
    public string? ResultId { get; set; }

    // channel post
    public long? ChannelId { get; set; }
    public int? MsgId { get; set; }

    // url
    public string? Url { get; set; }

    // weather
    public string? Emoji { get; set; }
    public double? Temperature { get; set; }
    public int? Color { get; set; }

    // star gift
    public string? Slug { get; set; }

    // suggested reaction
    public string? ReactionEmoticon { get; set; }
    public long? ReactionDocumentId { get; set; }
    public bool Dark { get; set; }
    public bool Flipped { get; set; }
}

/// <summary>
/// Stored discriminator for <see cref="StoryMediaArea.Type"/>. Persisted in MongoDB — keep stable.
/// </summary>
public static class StoryMediaAreaType
{
    public const int Venue = 0;
    public const int InputVenue = 1;
    public const int GeoPoint = 2;
    public const int SuggestedReaction = 3;
    public const int ChannelPost = 4;
    public const int InputChannelPost = 5;
    public const int Url = 6;
    public const int Weather = 7;
    public const int StarGift = 8;
}
