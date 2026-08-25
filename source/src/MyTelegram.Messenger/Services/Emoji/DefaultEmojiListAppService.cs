using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Emoji;

public interface IDefaultEmojiListAppService
{
    /// <summary>
    /// Answers one of the <c>emojiList</c> methods. <paramref name="requestHash"/> is the value the
    /// client is quoting back from its cached copy; a match is answered with
    /// <c>emojiListNotModified</c>.
    /// </summary>
    Task<IEmojiList> GetAsync(DefaultEmojiListKind kind, long requestHash);
}

/// <summary>
/// Serves the curated custom-emoji lists behind the profile-photo, group-photo and accent-colour
/// pickers, from the <c>default_emoji_lists</c> collection seeded by
/// <c>scripts/seed_default_emoji_lists.py</c>.
///
/// <para>The lists are Telegram's own selection — there is no rule that derives them from the
/// installed sets — so they are stored as an ordered list of document ids and served in that order.
/// An id whose document is missing from <c>eventflow-documentreadmodel</c> is dropped rather than
/// served: <c>messages.getCustomEmojiDocuments</c> would not resolve it and the client would draw a
/// blank tile in the grid.</para>
/// </summary>
public class DefaultEmojiListAppService(
    IMongoDatabase mongoDatabase,
    ILogger<DefaultEmojiListAppService> logger)
    : IDefaultEmojiListAppService, ITransientDependency
{
    private const string CollectionName = "default_emoji_lists";
    private const string DocumentCollectionName = "eventflow-documentreadmodel";

    public async Task<IEmojiList> GetAsync(DefaultEmojiListKind kind, long requestHash)
    {
        var documentIds = await LoadDocumentIdsAsync(kind);
        var hash = EmojiListHashHelper.ComputeHash(documentIds);

        // A zero hash is the client's "nothing cached" value, so it can never match — and an empty
        // list hashes to zero, which keeps a client from being told notModified about nothing.
        if (hash != 0 && requestHash == hash)
        {
            return new TEmojiListNotModified();
        }

        if (documentIds.Count == 0)
        {
            logger.LogWarning(
                "No custom emoji seeded for {Kind}; the picker will be empty. Run scripts/seed_default_emoji_lists.py",
                kind);
        }

        return new TEmojiList
        {
            Hash = hash,
            DocumentId = new TVector<long>(documentIds)
        };
    }

    private async Task<List<long>> LoadDocumentIdsAsync(DefaultEmojiListKind kind)
    {
        var key = GetStorageKey(kind);
        var collection = mongoDatabase.GetCollection<BsonDocument>(CollectionName);
        var document = await collection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", key))
            .FirstOrDefaultAsync();

        if (document == null || !document.TryGetValue("DocumentIds", out var raw) || !raw.IsBsonArray)
        {
            return [];
        }

        var documentIds = raw.AsBsonArray
            .Where(x => x.IsNumeric)
            .Select(x => x.ToInt64())
            .Where(x => x != 0)
            .Distinct()
            .ToList();

        if (documentIds.Count == 0)
        {
            return documentIds;
        }

        var documents = mongoDatabase.GetCollection<BsonDocument>(DocumentCollectionName);
        var present = await documents
            .Find(Builders<BsonDocument>.Filter.In("DocumentId", documentIds))
            .Project(Builders<BsonDocument>.Projection.Include("DocumentId").Exclude("_id"))
            .ToListAsync();

        var known = present
            .Where(x => x.TryGetValue("DocumentId", out var value) && value.IsNumeric)
            .Select(x => x["DocumentId"].ToInt64())
            .ToHashSet();

        // Keep the stored order: the client renders the grid exactly as it is served.
        var usable = documentIds.Where(known.Contains).ToList();

        if (usable.Count != documentIds.Count)
        {
            logger.LogWarning(
                "{Missing} of {Total} custom emoji listed for {Kind} are not in the document read model and were dropped",
                documentIds.Count - usable.Count, documentIds.Count, kind);
        }

        return usable;
    }

    private static string GetStorageKey(DefaultEmojiListKind kind) => kind switch
    {
        DefaultEmojiListKind.ProfilePhoto => "profile_photo",
        DefaultEmojiListKind.GroupPhoto => "group_photo",
        DefaultEmojiListKind.Background => "background",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}
