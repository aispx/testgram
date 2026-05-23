using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

internal sealed class GetPeerMaxIDsHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Stories.RequestGetPeerMaxIDs, TVector<MyTelegram.Schema.IRecentStory>>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");

    protected override async Task<TVector<MyTelegram.Schema.IRecentStory>> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Stories.RequestGetPeerMaxIDs obj)
    {
        var result = new TVector<IRecentStory>();
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var peer in obj.Id)
        {
            var (peerId, peerType) = StoryHelper.ResolvePeer(peer, input.UserId);

            var filter = Builders<StoryDocument>.Filter.And(
                Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, peerId),
                Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, peerType),
                Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false),
                Builders<StoryDocument>.Filter.Gte(s => s.ExpireDate, currentTime)
            );

            var maxStory = await _storyCollection.Find(filter)
                .SortByDescending(s => s.StoryId)
                .Limit(1)
                .FirstOrDefaultAsync();

            if (maxStory != null)
            {
                result.Add(new TRecentStory
                {
                    MaxId = maxStory.StoryId,
                    Live = maxStory.IsLive
                });
            }
            else
            {
                result.Add(new TRecentStory { });
            }
        }

        return result;
    }
}
