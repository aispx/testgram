using MongoDB.Bson.Serialization.Attributes;

namespace MyTelegram.Messenger.Services.Stories;

/// <summary>
/// The <a href="https://corefork.telegram.org/api/privacy">close friends list</a> of a user, set via
/// contacts.editCloseFriends and read when evaluating the <c>privacyValueAllowCloseFriends</c> story rule.
/// Collection: <c>close_friends</c>.
/// </summary>
public class CloseFriendDocument
{
    /// <summary><c>close-{selfUserId}</c>.</summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public long SelfUserId { get; set; }

    public List<long> UserIds { get; set; } = [];

    public static string BuildId(long selfUserId) => $"close-{selfUserId}";
}
