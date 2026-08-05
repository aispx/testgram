using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Converters;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Services.Stories;

/// <summary>The peers referenced by a set of stories, ready to be attached to a TL response.</summary>
public sealed class StoryPeerBundle
{
    public TVector<IUser> Users { get; init; } = new();
    public TVector<IChat> Chats { get; init; } = new();
}

public interface IStoryResponseBuilder
{
    /// <summary>
    /// Resolves the users and chats a set of stories refers to (owners and forward sources), so that
    /// responses never come back with an empty <c>users</c>/<c>chats</c> the client cannot render.
    /// </summary>
    Task<StoryPeerBundle> BuildPeersAsync(
        IRequestInput input,
        IEnumerable<StoryDocument> stories,
        IEnumerable<long>? extraUserIds = null);

    /// <summary>
    /// Loads the requesting user's own reactions for a set of stories in one query, keyed by story id.
    /// </summary>
    Task<Dictionary<int, IReaction>> GetSentReactionsAsync(
        long ownerPeerId,
        int ownerPeerType,
        IEnumerable<int> storyIds,
        long requestingUserId);

    /// <summary>Same as <see cref="GetSentReactionsAsync"/> for stories spanning several owners.</summary>
    Task<Dictionary<(long ownerPeerId, int ownerPeerType, int storyId), IReaction>> GetSentReactionsAsync(
        IEnumerable<StoryDocument> stories,
        long requestingUserId);

    /// <summary>
    /// Loads the real photo metadata for a set of stories in one query, keyed by photo id.
    /// </summary>
    /// <remarks>
    /// A story document stores only the media id, access hash and file reference — not the per-size
    /// breakdown. Without the photo read model the response has to guess the sizes, and a guessed
    /// <c>size</c> makes the client request byte ranges the file does not have, so the image never
    /// renders. Photo-type stories on this server carry <c>MediaSize = 0</c>, which is exactly the
    /// case the guess used to cover.
    /// </remarks>
    Task<Dictionary<long, IPhotoReadModel>> GetStoryPhotosAsync(IEnumerable<StoryDocument> stories);

    /// <summary>
    /// Loads the real document metadata for a set of video stories in one query, keyed by document id.
    /// </summary>
    /// <remarks>
    /// A story document has no thumbnail of its own unless one was captured at upload time, and most
    /// were not. The document read model does carry the thumbnail sizes, and the matching objects
    /// exist in storage — so without this the response ships an empty <c>thumbs</c> vector and the
    /// client has nothing to draw as the video's preview tile.
    /// </remarks>
    Task<Dictionary<long, IDocumentReadModel>> GetStoryDocumentsAsync(IEnumerable<StoryDocument> stories);
}

/// <summary>
/// Shared assembly of the repetitive parts of a stories.* response.
/// </summary>
public class StoryResponseBuilder(
    IMongoDatabase mongoDatabase,
    IUserConverterService userConverterService,
    IChatConverterService chatConverterService,
    IQueryProcessor queryProcessor)
    : IStoryResponseBuilder, ITransientDependency
{
    private readonly IMongoCollection<BsonDocument> _reactionsCollection =
        mongoDatabase.GetCollection<BsonDocument>("story_reactions");

    private readonly IMongoCollection<DocumentReadModel> _documentCollection =
        mongoDatabase.GetCollection<DocumentReadModel>("eventflow-documentreadmodel");

    public async Task<StoryPeerBundle> BuildPeersAsync(
        IRequestInput input,
        IEnumerable<StoryDocument> stories,
        IEnumerable<long>? extraUserIds = null)
    {
        var userIds = new HashSet<long>();
        var channelIds = new HashSet<long>();

        foreach (var story in stories)
        {
            AddPeer(story.OwnerPeerType, story.OwnerPeerId);

            if (story.FwdFromPeerId != 0)
            {
                AddPeer(story.FwdFromPeerType, story.FwdFromPeerId);
            }
        }

        if (extraUserIds != null)
        {
            foreach (var userId in extraUserIds)
            {
                if (userId > 0)
                {
                    userIds.Add(userId);
                }
            }
        }

        var users = new TVector<IUser>();
        var chats = new TVector<IChat>();

        if (userIds.Count > 0)
        {
            var userList = await userConverterService.GetUserListAsync(
                input, userIds.ToList(), false, false, input.Layer);
            foreach (var user in userList)
            {
                users.Add((IUser)user);
            }
        }

        if (channelIds.Count > 0)
        {
            var channelIdList = channelIds.ToList();
            var channelMemberReadModels = await queryProcessor.ProcessAsync(
                new GetChannelMemberListByChannelIdListQuery(input.UserId, channelIdList));
            var channelList = await chatConverterService.GetChannelListAsync(
                input, channelIdList, channelMemberReadModels, input.Layer);
            foreach (var chat in channelList)
            {
                chats.Add(chat);
            }
        }

        return new StoryPeerBundle { Users = users, Chats = chats };

        void AddPeer(int peerType, long peerId)
        {
            if (peerId <= 0)
            {
                return;
            }

            if (peerType == StoryHelper.PeerTypeChannel)
            {
                channelIds.Add(peerId);
            }
            else if (peerType == StoryHelper.PeerTypeUser)
            {
                userIds.Add(peerId);
            }
        }
    }

    public async Task<Dictionary<int, IReaction>> GetSentReactionsAsync(
        long ownerPeerId,
        int ownerPeerType,
        IEnumerable<int> storyIds,
        long requestingUserId)
    {
        var ids = storyIds.Distinct().ToList();
        var result = new Dictionary<int, IReaction>();

        if (ids.Count == 0)
        {
            return result;
        }

        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("storyOwnerPeerId", ownerPeerId),
            Builders<BsonDocument>.Filter.Eq("storyOwnerPeerType", ownerPeerType),
            Builders<BsonDocument>.Filter.Eq("userId", requestingUserId),
            Builders<BsonDocument>.Filter.In("storyId", ids.Select(id => (BsonValue)id))
        );

        var docs = await _reactionsCollection.Find(filter).ToListAsync();

        foreach (var doc in docs)
        {
            var reaction = ToReaction(doc);
            if (reaction != null && doc.Contains("storyId"))
            {
                result[doc["storyId"].AsInt32] = reaction;
            }
        }

        return result;
    }

    public async Task<Dictionary<(long, int, int), IReaction>> GetSentReactionsAsync(
        IEnumerable<StoryDocument> stories,
        long requestingUserId)
    {
        var storyList = stories.ToList();
        var result = new Dictionary<(long, int, int), IReaction>();

        if (storyList.Count == 0)
        {
            return result;
        }

        // One query covering every owner, matched back up in memory.
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("userId", requestingUserId),
            Builders<BsonDocument>.Filter.In(
                "storyOwnerPeerId",
                storyList.Select(s => (BsonValue)s.OwnerPeerId).Distinct()),
            Builders<BsonDocument>.Filter.In(
                "storyId",
                storyList.Select(s => (BsonValue)s.StoryId).Distinct())
        );

        var docs = await _reactionsCollection.Find(filter).ToListAsync();

        foreach (var doc in docs)
        {
            if (!doc.Contains("storyId") || !doc.Contains("storyOwnerPeerId") || !doc.Contains("storyOwnerPeerType"))
            {
                continue;
            }

            var reaction = ToReaction(doc);
            if (reaction == null)
            {
                continue;
            }

            var key = (
                doc["storyOwnerPeerId"].AsInt64,
                doc["storyOwnerPeerType"].AsInt32,
                doc["storyId"].AsInt32);

            result[key] = reaction;
        }

        return result;
    }

    internal static IReaction? ToReaction(BsonDocument doc)
    {
        if (!doc.Contains("reaction") || doc["reaction"].IsBsonNull)
        {
            return null;
        }

        var value = doc["reaction"].AsString;
        var type = doc.Contains("type") ? doc["type"].AsString : "emoji";

        if (type == "custom")
        {
            return long.TryParse(value, out var documentId)
                ? new TReactionCustomEmoji { DocumentId = documentId }
                : null;
        }

        return new TReactionEmoji { Emoticon = value };
    }

    public async Task<Dictionary<long, IPhotoReadModel>> GetStoryPhotosAsync(IEnumerable<StoryDocument> stories)
    {
        var photoIds = stories
            .Where(s => s.MediaType == StoryHelper.MediaTypePhoto && s.MediaFileId != 0)
            .Select(s => s.MediaFileId)
            .Distinct()
            .ToList();

        if (photoIds.Count == 0)
        {
            return [];
        }

        var photos = await queryProcessor.ProcessAsync(new GetPhotosByPhotoIdLisQuery(photoIds));

        return photos
            .GroupBy(p => p.PhotoId)
            .ToDictionary(g => g.Key, g => g.First());
    }

    public async Task<Dictionary<long, IDocumentReadModel>> GetStoryDocumentsAsync(IEnumerable<StoryDocument> stories)
    {
        var documentIds = stories
            .Where(s => s.MediaType == StoryHelper.MediaTypeVideo && s.MediaFileId != 0)
            .Select(s => s.MediaFileId)
            .Distinct()
            .ToList();

        if (documentIds.Count == 0)
        {
            return [];
        }

        // Read the collection directly: GetDocumentsByIdListQuery is declared but has no registered
        // handler, so dispatching it would throw at runtime.
        var documents = await _documentCollection
            .Find(Builders<DocumentReadModel>.Filter.In(d => d.DocumentId, documentIds))
            .ToListAsync();

        return documents
            .GroupBy(d => d.DocumentId)
            .ToDictionary(g => g.Key, g => (IDocumentReadModel)g.First());
    }
}
