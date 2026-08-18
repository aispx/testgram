using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Discussion;

/// <summary>
/// One user muted in another user's <c>@replies</c> peer.
/// See https://corefork.telegram.org/method/contacts.blockFromReplies
/// </summary>
public class RepliesBlockDocument
{
    /// <summary><c>{userId}-{blockedUserId}</c>.</summary>
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public string Id { get; set; } = string.Empty;

    /// <summary>The user who no longer wants to be notified.</summary>
    public long UserId { get; set; }

    /// <summary>The commenter whose replies are dropped.</summary>
    public long BlockedUserId { get; set; }

    /// <summary>Set when the block came with <c>report_spam</c>.</summary>
    public bool ReportedSpam { get; set; }

    public int Date { get; set; }
}

public interface IRepliesBlockService
{
    Task BlockAsync(long userId, long blockedUserId, bool reportSpam = false);
    Task<bool> IsBlockedAsync(long userId, long blockedUserId);
}

public class RepliesBlockService(IMongoDatabase database) : IRepliesBlockService, ITransientDependency
{
    private const string CollectionName = "replies_blocked";

    private IMongoCollection<RepliesBlockDocument> Collection =>
        database.GetCollection<RepliesBlockDocument>(CollectionName);

    public async Task BlockAsync(long userId, long blockedUserId, bool reportSpam = false)
    {
        var document = new RepliesBlockDocument
        {
            Id = $"{userId}-{blockedUserId}",
            UserId = userId,
            BlockedUserId = blockedUserId,
            ReportedSpam = reportSpam,
            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        await Collection.ReplaceOneAsync(p => p.Id == document.Id, document, new ReplaceOptions { IsUpsert = true });
    }

    public async Task<bool> IsBlockedAsync(long userId, long blockedUserId)
    {
        return await Collection.Find(p => p.Id == $"{userId}-{blockedUserId}").AnyAsync();
    }
}
