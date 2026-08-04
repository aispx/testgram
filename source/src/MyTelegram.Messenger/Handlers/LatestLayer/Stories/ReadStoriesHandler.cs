using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

/// <summary>
/// Mark all stories up to a certain ID as read, for a given peer; will emit an
/// <a href="https://corefork.telegram.org/constructor/updateReadStories">updateReadStories</a> update to all logged-in sessions.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stories.readStories"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// <para>
/// View counting goes through <see cref="IStoryViewRecorder"/>, which counts each (story, viewer) pair
/// at most once — reading a batch of N stories must not add N views to each of them.
/// </para>
/// </remarks>
internal sealed class ReadStoriesHandler(
    IMongoDatabase mongoDatabase,
    IStoryAccessService storyAccessService,
    IStoryViewRecorder storyViewRecorder,
    IStoryUpdatesSender storyUpdatesSender)
    : RpcResultObjectHandler<MyTelegram.Schema.Stories.RequestReadStories, TVector<int>>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");
    private readonly IMongoCollection<BsonDocument> _storyReadsCollection =
        mongoDatabase.GetCollection<BsonDocument>("story_reads");

    protected override async Task<TVector<int>> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Stories.RequestReadStories obj)
    {
        var (peerId, peerType) = await storyAccessService.ResolveReadablePeerAsync(obj.Peer, input.UserId);

        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var filter = Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, peerId),
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, peerType),
            Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false),
            Builders<StoryDocument>.Filter.Gte(s => s.ExpireDate, currentTime),
            Builders<StoryDocument>.Filter.Lte(s => s.StoryId, obj.MaxId)
        );

        var stories = await _storyCollection.Find(filter).ToListAsync();

        var context = await storyAccessService.GetViewerContextAsync(input.UserId, [peerId]);
        var visible = storyAccessService.FilterVisible(stories, input.UserId, context);

        await storyViewRecorder.RecordViewsAsync(
            peerId,
            peerType,
            visible.Select(s => s.StoryId),
            input.UserId,
            context.IsStealthActive(currentTime));

        await SaveMaxReadIdAsync(input.UserId, peerId, peerType, obj.MaxId, (int)currentTime);

        var readIds = visible.Select(s => s.StoryId).OrderBy(id => id).ToList();

        var updates = new TUpdates
        {
            Updates = new TVector<IUpdate>
            {
                new TUpdateReadStories
                {
                    Peer = StoryHelper.CreatePeer(peerType, peerId),
                    MaxId = obj.MaxId
                }
            },
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Date = CurrentDate
        };

        // The read marker is per account, so the caller's other sessions need it too.
        await storyUpdatesSender.PushToUserAsync(input.UserId, updates, input.AuthKeyId);

        return new TVector<int>(readIds);
    }

    /// <summary>
    /// Stores the read marker, never moving it backwards: an out-of-order request for an older story
    /// must not un-read newer ones.
    /// </summary>
    private async Task SaveMaxReadIdAsync(long userId, long peerId, int peerType, int maxId, int currentTime)
    {
        var readFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("userId", userId),
            Builders<BsonDocument>.Filter.Eq("ownerPeerId", peerId),
            Builders<BsonDocument>.Filter.Eq("ownerPeerType", peerType)
        );

        var existing = await _storyReadsCollection.Find(readFilter).FirstOrDefaultAsync();
        var existingMaxId = existing != null && existing.Contains("maxReadId") ? existing["maxReadId"].AsInt32 : 0;

        if (existingMaxId >= maxId)
        {
            return;
        }

        await _storyReadsCollection.ReplaceOneAsync(
            readFilter,
            new BsonDocument
            {
                { "userId", userId },
                { "ownerPeerId", peerId },
                { "ownerPeerType", peerType },
                { "maxReadId", maxId },
                { "date", currentTime }
            },
            new ReplaceOptions { IsUpsert = true });
    }
}
