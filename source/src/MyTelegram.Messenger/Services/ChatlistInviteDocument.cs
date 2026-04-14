using MongoDB.Bson.Serialization.Attributes;

namespace MyTelegram.Messenger.Services;

public class ChatlistInviteDocument
{
    [BsonId]
    public string Id { get; set; } = string.Empty; // "chatlist-invite-{slug}"

    public string Slug { get; set; } = string.Empty; // Unique URL slug

    public long CreatorUserId { get; set; }

    public int FilterId { get; set; } // Dialog filter ID

    public string Title { get; set; } = string.Empty;

    public List<long> PeerIds { get; set; } = new(); // Channel/group IDs included in invite

    public List<string> PeerTypes { get; set; } = new(); // "Channel" or "Chat"

    public int CreatedDate { get; set; } // Unix timestamp

    public bool Revoked { get; set; } = false;
}
