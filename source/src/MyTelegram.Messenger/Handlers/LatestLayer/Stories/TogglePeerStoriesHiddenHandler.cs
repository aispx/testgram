using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

/// <summary>
/// Hide or unhide the active stories of a specific peer, moving them between the main and the archived
/// stories bar.
/// <para><c>See <a href="https://corefork.telegram.org/method/stories.togglePeerStoriesHidden"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// <para>
/// The flag is keyed by <c>(userId, peerId, peerType)</c>. Channels own stories just like users do and
/// are grouped by owner peer type in stories.getAllStories, so the type has to be part of the key --
/// storing only the id would conflate a user and a channel that happen to share an id.
/// </para>
/// </remarks>
internal sealed class TogglePeerStoriesHiddenHandler(
    IMongoDatabase mongoDatabase,
    IStoryAccessService storyAccessService)
    : RpcResultObjectHandler<MyTelegram.Schema.Stories.RequestTogglePeerStoriesHidden, IBool>
{
    private readonly IMongoCollection<BsonDocument> _hiddenCollection =
        mongoDatabase.GetCollection<BsonDocument>("story_hidden_peers");

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Stories.RequestTogglePeerStoriesHidden obj)
    {
        // Validates that the peer exists and that the caller may see its stories at all, so the flag
        // cannot be written for an arbitrary id.
        var (peerId, peerType) = await storyAccessService.ResolveReadablePeerAsync(obj.Peer, input.UserId);

        // Only users and channels can own stories; ResolveReadablePeerAsync already rejects the rest.
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("userId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("peerId", peerId),
            // Rows written before peerType existed are all user peers, so a missing field reads as user.
            peerType == StoryHelper.PeerTypeUser
                ? Builders<BsonDocument>.Filter.Or(
                    Builders<BsonDocument>.Filter.Eq("peerType", peerType),
                    Builders<BsonDocument>.Filter.Exists("peerType", false))
                : Builders<BsonDocument>.Filter.Eq("peerType", peerType)
        );

        var doc = new BsonDocument
        {
            { "userId", input.UserId },
            { "peerId", peerId },
            { "peerType", peerType },
            { "hidden", obj.Hidden }
        };

        await _hiddenCollection.ReplaceOneAsync(filter, doc, new ReplaceOptions { IsUpsert = true });

        return new TBoolTrue();
    }
}
