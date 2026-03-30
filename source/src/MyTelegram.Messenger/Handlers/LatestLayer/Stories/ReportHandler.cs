using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;
internal sealed class ReportHandler(
    IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Stories.RequestReport, IReportResult>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");
    private readonly IMongoCollection<BsonDocument> _reportsCollection =
        mongoDatabase.GetCollection<BsonDocument>("story_reports");

    protected override async Task<IReportResult> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Stories.RequestReport obj)
    {
        var (peerId, peerType) = StoryHelper.ResolvePeer(obj.Peer, input.UserId);

        foreach (var storyId in obj.Id)
        {
            var reportDoc = new BsonDocument
            {
                { "reporterUserId", input.UserId },
                { "ownerPeerId", peerId },
                { "ownerPeerType", peerType },
                { "storyId", storyId },
                { "message", obj.Message ?? "" },
                { "date", DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
            };

            await _reportsCollection.InsertOneAsync(reportDoc);

            var filter = Builders<StoryDocument>.Filter.And(
                Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, peerId),
                Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, peerType),
                Builders<StoryDocument>.Filter.Eq(s => s.StoryId, storyId)
            );
            var update = Builders<StoryDocument>.Update.Set(s => s.Reported, true);
            await _storyCollection.UpdateOneAsync(filter, update);
        }

        return new TReportResultReported();
    }
}
