using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson;

namespace MyTelegram.Messenger.Services.Privacy;

public class PrivacyDocument
{
    public string Id { get; set; } = null!; // "{userId}:{privacyType}"
    public long UserId { get; set; }
    [BsonRepresentation(BsonType.Int32)]
    public PrivacyType PrivacyType { get; set; }
    public List<PrivacyRuleEntry> Rules { get; set; } = [];
}

public class PrivacyRuleEntry
{
    [BsonRepresentation(BsonType.Int32)]
    public PrivacyValueType ValueType { get; set; }
    public List<long>? UserIds { get; set; }
    public List<long>? ChatIds { get; set; }
}
