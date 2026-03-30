using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MyTelegram.Messenger.Services;

/// <summary>
/// Stored in MongoDB collection "bot-verifications"
/// </summary>
public class BotVerificationDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    public long BotId { get; set; }
    public long Icon { get; set; }
    public string Company { get; set; } = "";
    public string Description { get; set; } = "";

    // Target: either UserId or ChannelId (one is set, other is 0)
    public long UserId { get; set; }
    public long ChannelId { get; set; }
}
