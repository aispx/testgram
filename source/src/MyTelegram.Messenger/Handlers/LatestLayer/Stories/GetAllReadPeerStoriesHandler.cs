using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

internal sealed class GetAllReadPeerStoriesHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Stories.RequestGetAllReadPeerStories, MyTelegram.Schema.IUpdates>
{
    private readonly IMongoCollection<BsonDocument> _storyReadsCollection =
        mongoDatabase.GetCollection<BsonDocument>("story_reads");

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Stories.RequestGetAllReadPeerStories obj)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("userId", input.UserId);
        var reads = await _storyReadsCollection.Find(filter).ToListAsync();

        var updates = new TVector<IUpdate>();
        foreach (var r in reads)
        {
            if (r.Contains("ownerPeerId") && r.Contains("ownerPeerType"))
            {
                var peerId = r["ownerPeerId"].AsInt64;
                var peerType = r["ownerPeerType"].AsInt32;
                var maxReadId = r.Contains("maxReadId") ? r["maxReadId"].AsInt32 : 0;

                updates.Add(new TUpdateReadStories
                {
                    Peer = StoryHelper.CreatePeer(peerType, peerId),
                    MaxId = maxReadId
                });
            }
        }

        return new TUpdates
        {
            Updates = updates,
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Date = CurrentDate,
            Seq = 0
        };
    }
}
