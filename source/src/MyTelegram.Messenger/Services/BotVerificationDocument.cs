using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MyTelegram.Messenger.Services;

public class BotVerificationDocument
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public long BotId { get; set; }

    public long Icon { get; set; }

    public string Company { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public long UserId { get; set; }

    public long ChannelId { get; set; }
}
