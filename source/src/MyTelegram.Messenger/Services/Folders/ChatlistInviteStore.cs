using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Folders;

/// <summary>
/// The exported <a href="https://corefork.telegram.org/api/links#chat-folder-links">chat folder links</a>
/// of a folder, and the invite an imported folder came from.
/// </summary>
public interface IChatlistInviteStore
{
    Task<ChatlistInviteDocument?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default);

    Task<List<ChatlistInviteDocument>> GetByFilterAsync(long creatorUserId, int filterId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the folder has at least one link that was not revoked — this is
    /// <c>dialogFilterChatlist.has_my_invites</c>.
    /// </summary>
    Task<bool> HasInvitesAsync(long creatorUserId, int filterId, CancellationToken cancellationToken = default);

    /// <summary>The folder ids of the caller that carry at least one live link.</summary>
    Task<HashSet<int>> GetFilterIdsWithInvitesAsync(long creatorUserId, CancellationToken cancellationToken = default);

    Task<long> CountByFilterAsync(long creatorUserId, int filterId, CancellationToken cancellationToken = default);

    Task InsertAsync(ChatlistInviteDocument invite, CancellationToken cancellationToken = default);

    Task<ChatlistInviteDocument?> UpdateAsync(string slug, long creatorUserId, int filterId, string? title,
        List<Peer>? peers, CancellationToken cancellationToken = default);

    Task<bool> RevokeAsync(string slug, long creatorUserId, int filterId,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class ChatlistInviteStore(IMongoDatabase database) : IChatlistInviteStore, ITransientDependency
{
    private IMongoCollection<ChatlistInviteDocument> Collection =>
        database.GetCollection<ChatlistInviteDocument>("chatlist_invites");

    public async Task<ChatlistInviteDocument?> GetBySlugAsync(string slug,
        CancellationToken cancellationToken = default)
    {
        return await Collection.Find(p => p.Slug == slug).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<List<ChatlistInviteDocument>> GetByFilterAsync(long creatorUserId, int filterId,
        CancellationToken cancellationToken = default)
    {
        return await Collection
            .Find(p => p.CreatorUserId == creatorUserId && p.FilterId == filterId && !p.Revoked)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> HasInvitesAsync(long creatorUserId, int filterId,
        CancellationToken cancellationToken = default)
    {
        return await CountByFilterAsync(creatorUserId, filterId, cancellationToken) > 0;
    }

    public async Task<HashSet<int>> GetFilterIdsWithInvitesAsync(long creatorUserId,
        CancellationToken cancellationToken = default)
    {
        var invites = await Collection
            .Find(p => p.CreatorUserId == creatorUserId && !p.Revoked)
            .Project(p => p.FilterId)
            .ToListAsync(cancellationToken);

        return [.. invites];
    }

    public async Task<long> CountByFilterAsync(long creatorUserId, int filterId,
        CancellationToken cancellationToken = default)
    {
        return await Collection.CountDocumentsAsync(
            p => p.CreatorUserId == creatorUserId && p.FilterId == filterId && !p.Revoked,
            cancellationToken: cancellationToken);
    }

    public Task InsertAsync(ChatlistInviteDocument invite, CancellationToken cancellationToken = default)
    {
        return Collection.InsertOneAsync(invite, cancellationToken: cancellationToken);
    }

    public async Task<ChatlistInviteDocument?> UpdateAsync(string slug, long creatorUserId, int filterId,
        string? title, List<Peer>? peers, CancellationToken cancellationToken = default)
    {
        var updates = new List<UpdateDefinition<ChatlistInviteDocument>>();
        if (title != null)
        {
            updates.Add(Builders<ChatlistInviteDocument>.Update.Set(p => p.Title, title));
        }

        if (peers != null)
        {
            updates.Add(Builders<ChatlistInviteDocument>.Update.Set(p => p.PeerIds,
                [.. peers.Select(p => p.PeerId)]));
            updates.Add(Builders<ChatlistInviteDocument>.Update.Set(p => p.PeerTypes,
                [.. peers.Select(p => p.PeerType.ToString())]));
        }

        var filter = Builders<ChatlistInviteDocument>.Filter.Where(p =>
            p.Slug == slug && p.CreatorUserId == creatorUserId && p.FilterId == filterId);

        if (updates.Count == 0)
        {
            return await Collection.Find(filter).FirstOrDefaultAsync(cancellationToken);
        }

        return await Collection.FindOneAndUpdateAsync(filter,
            Builders<ChatlistInviteDocument>.Update.Combine(updates),
            new FindOneAndUpdateOptions<ChatlistInviteDocument> { ReturnDocument = ReturnDocument.After },
            cancellationToken);
    }

    public async Task<bool> RevokeAsync(string slug, long creatorUserId, int filterId,
        CancellationToken cancellationToken = default)
    {
        var result = await Collection.UpdateOneAsync(
            p => p.Slug == slug && p.CreatorUserId == creatorUserId && p.FilterId == filterId,
            Builders<ChatlistInviteDocument>.Update.Set(p => p.Revoked, true),
            cancellationToken: cancellationToken);

        return result.MatchedCount > 0;
    }
}
