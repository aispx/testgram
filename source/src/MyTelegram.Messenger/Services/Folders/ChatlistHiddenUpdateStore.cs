using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Folders;

/// <summary>
/// The peers a user dismissed with <c>chatlists.hideChatlistUpdates</c>, per imported folder.
/// </summary>
[BsonIgnoreExtraElements]
public class ChatlistHiddenUpdateDocument
{
    /// <summary><c>{UserId}:{FilterId}</c>.</summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public long UserId { get; set; }

    public int FilterId { get; set; }

    public List<long> HiddenPeerIds { get; set; } = [];

    public int Date { get; set; }

    public static string MakeId(long userId, int filterId) => $"{userId}:{filterId}";
}

/// <summary>
/// Remembers which peers of a shared folder the user does not want to be offered again:
/// "If after excluding inaccessible peers and peers deselected by the user the <c>peers</c> list is empty,
/// invoke chatlists.hideChatlistUpdates instead of chatlists.joinChatlistUpdates."
/// </summary>
public interface IChatlistHiddenUpdateStore
{
    Task<HashSet<long>> GetHiddenPeerIdsAsync(long userId, int filterId, CancellationToken cancellationToken = default);

    Task HideAsync(long userId, int filterId, IReadOnlyCollection<long> peerIds,
        CancellationToken cancellationToken = default);

    /// <summary>Called once a peer really made it into the folder, so it is no longer a pending update.</summary>
    Task UnhideAsync(long userId, int filterId, IReadOnlyCollection<long> peerIds,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(long userId, int filterId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class ChatlistHiddenUpdateStore(IMongoDatabase database) : IChatlistHiddenUpdateStore, ITransientDependency
{
    private IMongoCollection<ChatlistHiddenUpdateDocument> Collection =>
        database.GetCollection<ChatlistHiddenUpdateDocument>("chatlist_hidden_updates");

    public async Task<HashSet<long>> GetHiddenPeerIdsAsync(long userId, int filterId,
        CancellationToken cancellationToken = default)
    {
        var id = ChatlistHiddenUpdateDocument.MakeId(userId, filterId);
        var document = await Collection.Find(p => p.Id == id).FirstOrDefaultAsync(cancellationToken);

        return document == null ? [] : [.. document.HiddenPeerIds];
    }

    public Task HideAsync(long userId, int filterId, IReadOnlyCollection<long> peerIds,
        CancellationToken cancellationToken = default)
    {
        if (peerIds.Count == 0)
        {
            return Task.CompletedTask;
        }

        var id = ChatlistHiddenUpdateDocument.MakeId(userId, filterId);
        var update = Builders<ChatlistHiddenUpdateDocument>.Update
            .SetOnInsert(p => p.UserId, userId)
            .SetOnInsert(p => p.FilterId, filterId)
            .Set(p => p.Date, (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            .AddToSetEach(p => p.HiddenPeerIds, peerIds);

        return Collection.UpdateOneAsync(p => p.Id == id, update, new UpdateOptions { IsUpsert = true },
            cancellationToken);
    }

    public Task UnhideAsync(long userId, int filterId, IReadOnlyCollection<long> peerIds,
        CancellationToken cancellationToken = default)
    {
        if (peerIds.Count == 0)
        {
            return Task.CompletedTask;
        }

        var id = ChatlistHiddenUpdateDocument.MakeId(userId, filterId);

        return Collection.UpdateOneAsync(p => p.Id == id,
            Builders<ChatlistHiddenUpdateDocument>.Update.PullAll(p => p.HiddenPeerIds, peerIds),
            cancellationToken: cancellationToken);
    }

    public Task DeleteAsync(long userId, int filterId, CancellationToken cancellationToken = default)
    {
        var id = ChatlistHiddenUpdateDocument.MakeId(userId, filterId);

        return Collection.DeleteOneAsync(p => p.Id == id, cancellationToken);
    }
}
