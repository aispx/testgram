using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services;

/// <summary>
/// Temporary read-only access to a chat, handed out by messages.checkChatInvite so that the holder
/// of an invite link can read the history before deciding to join.
/// See https://corefork.telegram.org/constructor/chatInvitePeek
/// </summary>
public class ChatInvitePeekDocument
{
    /// <summary>
    /// <c>{userId}-{peerId}</c>: one peek window per user and chat.
    /// </summary>
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public string Id { get; set; } = string.Empty;

    public long UserId { get; set; }
    public long PeerId { get; set; }

    /// <summary>The invite hash the peek was granted for.</summary>
    public string InviteHash { get; set; } = string.Empty;

    /// <summary>Unixtime at which the read-only access stops working.</summary>
    public int Expires { get; set; }

    /// <summary>When the peek was granted.</summary>
    public int Date { get; set; }
}

public interface IChatInvitePeekService
{
    /// <summary>
    /// Grants read-only access to <paramref name="peerId"/>, or returns the window already running.
    /// </summary>
    Task<ChatInvitePeekDocument> GrantAsync(long userId, long peerId, string inviteHash);

    /// <summary>The user's peek window for <paramref name="peerId"/> if it has not run out, else null.</summary>
    Task<ChatInvitePeekDocument?> GetActiveAsync(long userId, long peerId);

    Task<bool> HasActivePeekAsync(long userId, long peerId);

    /// <summary>
    /// Ends the peek window, so that leaving the chat again takes read access away immediately.
    /// </summary>
    Task RevokeAsync(long userId, long peerId);
}

public class ChatInvitePeekService(IMongoDatabase database) : IChatInvitePeekService, ITransientDependency
{
    /// <summary>
    /// How long a peek lasts. The API does not mandate a value - it only says the client may read
    /// the chat until <c>expires</c> - so this is short enough that a revoked or deleted link stops
    /// granting access soon after, and long enough to read through a history.
    /// </summary>
    public const int PeekDuration = 30 * 60;

    private const string CollectionName = "chat_invite_peeks";

    private IMongoCollection<ChatInvitePeekDocument> Collection =>
        database.GetCollection<ChatInvitePeekDocument>(CollectionName);

    public async Task<ChatInvitePeekDocument> GrantAsync(long userId, long peerId, string inviteHash)
    {
        // Re-checking the same link must not keep pushing the deadline back, otherwise a client
        // that polls checkChatInvite would hold read access forever.
        var active = await GetActiveAsync(userId, peerId);
        if (active != null)
        {
            return active;
        }

        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var document = new ChatInvitePeekDocument
        {
            Id = $"{userId}-{peerId}",
            UserId = userId,
            PeerId = peerId,
            InviteHash = inviteHash,
            Expires = now + PeekDuration,
            Date = now
        };

        await Collection.ReplaceOneAsync(p => p.Id == document.Id, document, new ReplaceOptions { IsUpsert = true });

        return document;
    }

    public async Task<ChatInvitePeekDocument?> GetActiveAsync(long userId, long peerId)
    {
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return await Collection
            .Find(Builders<ChatInvitePeekDocument>.Filter.And(
                Builders<ChatInvitePeekDocument>.Filter.Eq(p => p.Id, $"{userId}-{peerId}"),
                Builders<ChatInvitePeekDocument>.Filter.Gt(p => p.Expires, now)))
            .FirstOrDefaultAsync();
    }

    public async Task<bool> HasActivePeekAsync(long userId, long peerId)
    {
        return await GetActiveAsync(userId, peerId) != null;
    }

    public async Task RevokeAsync(long userId, long peerId)
    {
        await Collection.DeleteOneAsync(p => p.Id == $"{userId}-{peerId}");
    }
}
