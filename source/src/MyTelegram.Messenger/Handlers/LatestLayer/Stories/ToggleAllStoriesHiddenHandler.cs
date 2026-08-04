using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

/// <summary>
/// Hide or unhide the active stories of every peer at once, moving them between the main and the
/// archived stories bar.
/// <para><c>See <a href="https://corefork.telegram.org/method/stories.toggleAllStoriesHidden"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// <para>
/// Stored in the same <c>story_hidden_peers</c> collection as the per-peer flag, under the reserved
/// peer id 0, so that stories.getAllStories only needs one lookup to resolve both.
/// </para>
/// </remarks>
internal sealed class ToggleAllStoriesHiddenHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Stories.RequestToggleAllStoriesHidden, IBool>
{
    /// <summary>Reserved peer id marking the "all peers" row.</summary>
    internal const long AllPeersId = 0;

    private readonly IMongoCollection<BsonDocument> _hiddenCollection =
        mongoDatabase.GetCollection<BsonDocument>("story_hidden_peers");

    protected override async Task<IBool> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Stories.RequestToggleAllStoriesHidden obj)
    {
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("userId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("peerId", AllPeersId)
        );

        var doc = new BsonDocument
        {
            { "userId", input.UserId },
            { "peerId", AllPeersId },
            { "hidden", obj.Hidden },
            { "allHidden", true },
            { "date", DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
        };

        await _hiddenCollection.ReplaceOneAsync(filter, doc, new ReplaceOptions { IsUpsert = true });

        return new TBoolTrue();
    }
}
