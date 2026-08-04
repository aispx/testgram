using MongoDB.Bson.Serialization.Attributes;

namespace MyTelegram.Messenger.Services.Stories;

/// <summary>
/// A <a href="https://corefork.telegram.org/api/stories#story-albums">story album</a> on a profile.
/// Albums exist independently of their stories, so an emptied album still shows up in stories.getAlbums.
/// Collection: <c>story_albums</c>.
/// </summary>
public class StoryAlbumDocument
{
    /// <summary><c>album-{ownerPeerType}-{ownerPeerId}-{albumId}</c>.</summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public long OwnerPeerId { get; set; }
    public int OwnerPeerType { get; set; }
    public int AlbumId { get; set; }
    public string Title { get; set; } = string.Empty;

    /// <summary>Display order on the profile, see stories.reorderAlbums. Lower comes first.</summary>
    public int Order { get; set; }

    /// <summary>Story whose media is used as the album cover; 0 when the album has no stories.</summary>
    public int IconStoryId { get; set; }

    /// <summary>
    /// Explicit story order inside the album, set by the <c>order</c> field of stories.updateAlbum.
    /// Stories absent from this list are listed after it, newest first.
    /// </summary>
    public List<int> StoryOrder { get; set; } = [];

    public long Date { get; set; }

    public static string BuildId(int ownerPeerType, long ownerPeerId, int albumId)
    {
        return $"album-{ownerPeerType}-{ownerPeerId}-{albumId}";
    }
}
