using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;

internal sealed class GetGroupCallStreamRtmpUrlHandler(
    IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<RequestGetGroupCallStreamRtmpUrl, IGroupCallStreamRtmpUrl>
{
    private readonly IMongoCollection<BsonDocument> _groupCallCollection =
        mongoDatabase.GetCollection<BsonDocument>("group_calls");

    protected override async Task<IGroupCallStreamRtmpUrl> HandleCoreAsync(IRequestInput input, RequestGetGroupCallStreamRtmpUrl obj)
    {
        var (peerId, peerType) = StoryHelper.ResolvePeer(obj.Peer, input.UserId);

        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("peer_id", peerId),
            Builders<BsonDocument>.Filter.Eq("peer_type", peerType),
            Builders<BsonDocument>.Filter.Eq("is_live_story", true)
        );

        var groupCall = await _groupCallCollection.Find(filter).FirstOrDefaultAsync();
        if (groupCall == null)
        {
            return new TGroupCallStreamRtmpUrl
            {
                Url = "rtmp://testgram.b2t.pro:1935/live",
                Key = Guid.NewGuid().ToString("N")
            };
        }

        var rtmpUrl = groupCall.GetValue("rtmp_url", "rtmp://testgram.b2t.pro:1935/live").AsString;
        var streamKey = groupCall.GetValue("rtmp_stream_key", Guid.NewGuid().ToString("N")).AsString;

        if (obj.Revoke)
        {
            streamKey = Guid.NewGuid().ToString("N");
            var update = Builders<BsonDocument>.Update.Set("rtmp_stream_key", streamKey);
            await _groupCallCollection.UpdateOneAsync(filter, update);
        }

        return new TGroupCallStreamRtmpUrl
        {
            Url = rtmpUrl,
            Key = streamKey
        };
    }
}