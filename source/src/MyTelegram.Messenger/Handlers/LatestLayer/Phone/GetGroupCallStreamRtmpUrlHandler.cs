using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;

internal sealed class GetGroupCallStreamRtmpUrlHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options)
    : RpcResultObjectHandler<RequestGetGroupCallStreamRtmpUrl, IGroupCallStreamRtmpUrl>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");
    private readonly IMongoCollection<GroupCallRtmpStreamDocument> _rtmpStreamCollection =
        mongoDatabase.GetCollection<GroupCallRtmpStreamDocument>("group_call_rtmp_streams");
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");

    protected override async Task<IGroupCallStreamRtmpUrl> HandleCoreAsync(IRequestInput input, RequestGetGroupCallStreamRtmpUrl obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            return null!;
        }

        var defaultRtmpUrl = GroupCallRtmpHelper.GetRtmpStreamUrl(options.CurrentValue);
        if (obj.LiveStory)
        {
            return await GetLiveStoryRtmpUrlAsync(peer, obj.Revoke, defaultRtmpUrl);
        }

        var filter = Builders<GroupCallDocument>.Filter.And(
            Builders<GroupCallDocument>.Filter.Eq(call => call.PeerId, peer.PeerId),
            Builders<GroupCallDocument>.Filter.Eq(call => call.PeerType, (int)peer.PeerType),
            Builders<GroupCallDocument>.Filter.Eq(call => call.RtmpStream, true));

        var groupCall = await _groupCallCollection.Find(filter)
            .SortByDescending(call => call.Date)
            .FirstOrDefaultAsync();
        if (groupCall == null)
        {
            var id = GroupCallRtmpHelper.GetStreamId(peer.PeerId, (int)peer.PeerType);
            var saved = await _rtmpStreamCollection.Find(stream => stream.Id == id).FirstOrDefaultAsync();
            if (saved == null || obj.Revoke)
            {
                saved = new GroupCallRtmpStreamDocument
                {
                    Id = id,
                    PeerId = peer.PeerId,
                    PeerType = (int)peer.PeerType,
                    Url = defaultRtmpUrl,
                    Key = GroupCallRtmpHelper.CreateStreamKey(),
                    Date = GroupCallStateHelper.CurrentDate()
                };
                await _rtmpStreamCollection.ReplaceOneAsync(
                    stream => stream.Id == id,
                    saved,
                    new ReplaceOptions { IsUpsert = true });
            }

            return new TGroupCallStreamRtmpUrl
            {
                Url = GroupCallRtmpHelper.GetStoredOrDefault(saved.Url, defaultRtmpUrl),
                Key = saved.Key
            };
        }

        var rtmpUrl = GroupCallRtmpHelper.GetStoredOrDefault(groupCall.RtmpUrl, defaultRtmpUrl);
        var streamKey = groupCall.RtmpStreamKey;
        if (obj.Revoke || string.IsNullOrWhiteSpace(streamKey))
        {
            streamKey = GroupCallRtmpHelper.CreateStreamKey();
            var update = Builders<GroupCallDocument>.Update
                .Set(call => call.RtmpUrl, rtmpUrl)
                .Set(call => call.RtmpStreamKey, streamKey)
                .Inc(call => call.Version, 1);
            await _groupCallCollection.UpdateOneAsync(call => call.CallId == groupCall.CallId, update);
        }

        return new TGroupCallStreamRtmpUrl
        {
            Url = rtmpUrl,
            Key = streamKey
        };
    }

    private async Task<IGroupCallStreamRtmpUrl> GetLiveStoryRtmpUrlAsync(
        Peer peer,
        bool revoke,
        string defaultRtmpUrl)
    {
        var storyPeerType = StoryHelper.ToStoryPeerType(peer.PeerType);
        if (storyPeerType < 0)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            return null!;
        }

        var currentDate = GroupCallStateHelper.CurrentDate();
        var liveStory = await _storyCollection.Find(story =>
                story.OwnerPeerId == peer.PeerId &&
                story.OwnerPeerType == storyPeerType &&
                story.IsLive &&
                !story.Deleted &&
                story.ExpireDate >= currentDate)
            .SortByDescending(story => story.StoryId)
            .FirstOrDefaultAsync();
        if (liveStory != null)
        {
            var rtmpUrl = GroupCallRtmpHelper.GetStoredOrDefault(liveStory.RtmpUrl, defaultRtmpUrl);
            var streamKey = liveStory.RtmpStreamKey;
            if (revoke || string.IsNullOrWhiteSpace(streamKey))
            {
                streamKey = GroupCallRtmpHelper.CreateStreamKey();
                await _storyCollection.UpdateOneAsync(
                    story => story.Id == liveStory.Id,
                    Builders<StoryDocument>.Update
                        .Set(story => story.RtmpUrl, rtmpUrl)
                        .Set(story => story.RtmpStreamKey, streamKey)
                        .Set(story => story.RtmpStream, true));

                await _groupCallCollection.UpdateOneAsync(
                    call => call.CallId == liveStory.GroupCallId,
                    Builders<GroupCallDocument>.Update
                        .Set(call => call.RtmpStream, true)
                        .Set(call => call.RtmpUrl, rtmpUrl)
                        .Set(call => call.RtmpStreamKey, streamKey)
                        .Inc(call => call.Version, 1));
            }

            return new TGroupCallStreamRtmpUrl
            {
                Url = rtmpUrl,
                Key = streamKey!
            };
        }

        var id = GroupCallRtmpHelper.GetStreamId(peer.PeerId, (int)peer.PeerType, liveStory: true);
        var saved = await _rtmpStreamCollection.Find(stream => stream.Id == id).FirstOrDefaultAsync();
        if (saved == null || revoke)
        {
            saved = new GroupCallRtmpStreamDocument
            {
                Id = id,
                PeerId = peer.PeerId,
                PeerType = (int)peer.PeerType,
                Url = defaultRtmpUrl,
                Key = GroupCallRtmpHelper.CreateStreamKey(),
                Date = currentDate
            };
            await _rtmpStreamCollection.ReplaceOneAsync(
                stream => stream.Id == id,
                saved,
                new ReplaceOptions { IsUpsert = true });
        }

        return new TGroupCallStreamRtmpUrl
        {
            Url = GroupCallRtmpHelper.GetStoredOrDefault(saved.Url, defaultRtmpUrl),
            Key = saved.Key
        };
    }
}
