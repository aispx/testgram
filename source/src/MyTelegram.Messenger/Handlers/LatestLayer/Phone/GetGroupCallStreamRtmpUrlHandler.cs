using MongoDB.Driver;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;

internal sealed class GetGroupCallStreamRtmpUrlHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    IChannelAdminRightsChecker channelAdminRightsChecker)
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

        await CheckCanGetRtmpCredentialsAsync(peer, input.UserId, obj.LiveStory);

        var defaultRtmpUrl = GroupCallRtmpHelper.GetRtmpStreamUrl(options.CurrentValue);
        if (obj.LiveStory)
        {
            return await GetLiveStoryRtmpUrlAsync(peer, obj.Revoke, defaultRtmpUrl);
        }

        var filter = Builders<GroupCallDocument>.Filter.And(
            Builders<GroupCallDocument>.Filter.Eq(call => call.PeerId, peer.PeerId),
            Builders<GroupCallDocument>.Filter.Eq(call => call.PeerType, (int)peer.PeerType),
            Builders<GroupCallDocument>.Filter.Eq(call => call.RtmpStream, true),
            Builders<GroupCallDocument>.Filter.Eq(call => call.Active, true));

        var groupCall = await _groupCallCollection.Find(filter)
            .SortByDescending(call => call.Date)
            .FirstOrDefaultAsync();

        var streamId = GroupCallRtmpHelper.GetStreamId(peer.PeerId, (int)peer.PeerType);
        var saved = await GetOrCreateSavedStreamAsync(
            streamId,
            peer.PeerId,
            (int)peer.PeerType,
            defaultRtmpUrl,
            obj.Revoke,
            groupCall?.RtmpStreamKey);

        if (groupCall != null &&
            (groupCall.RtmpUrl != saved.Url ||
             groupCall.RtmpStreamKey != saved.Key ||
             !groupCall.RtmpStream))
        {
            var update = Builders<GroupCallDocument>.Update
                .Set(call => call.RtmpStream, true)
                .Set(call => call.RtmpUrl, saved.Url)
                .Set(call => call.RtmpStreamKey, saved.Key)
                .Inc(call => call.Version, 1);
            await _groupCallCollection.UpdateOneAsync(call => call.CallId == groupCall.CallId, update);
        }

        return new TGroupCallStreamRtmpUrl
        {
            Url = saved.Url,
            Key = saved.Key
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
        var streamId = GroupCallRtmpHelper.GetStreamId(peer.PeerId, (int)peer.PeerType, liveStory: true);
        if (liveStory != null)
        {
            var saved = await GetOrCreateSavedStreamAsync(
                streamId,
                peer.PeerId,
                (int)peer.PeerType,
                defaultRtmpUrl,
                revoke,
                liveStory.RtmpStreamKey,
                currentDate);

            if (liveStory.RtmpUrl != saved.Url ||
                liveStory.RtmpStreamKey != saved.Key ||
                !liveStory.RtmpStream)
            {
                await _storyCollection.UpdateOneAsync(
                    story => story.Id == liveStory.Id,
                    Builders<StoryDocument>.Update
                        .Set(story => story.RtmpUrl, saved.Url)
                        .Set(story => story.RtmpStreamKey, saved.Key)
                        .Set(story => story.RtmpStream, true));

                await _groupCallCollection.UpdateOneAsync(
                    call => call.CallId == liveStory.GroupCallId,
                    Builders<GroupCallDocument>.Update
                        .Set(call => call.RtmpStream, true)
                        .Set(call => call.RtmpUrl, saved.Url)
                        .Set(call => call.RtmpStreamKey, saved.Key)
                        .Inc(call => call.Version, 1));
            }

            return new TGroupCallStreamRtmpUrl
            {
                Url = saved.Url,
                Key = saved.Key
            };
        }

        var newSaved = await GetOrCreateSavedStreamAsync(
            streamId,
            peer.PeerId,
            (int)peer.PeerType,
            defaultRtmpUrl,
            revoke,
            date: currentDate);

        return new TGroupCallStreamRtmpUrl
        {
            Url = newSaved.Url,
            Key = newSaved.Key
        };
    }

    private async Task CheckCanGetRtmpCredentialsAsync(Peer peer, long userId, bool liveStory)
    {
        if (peer.PeerType != PeerType.Channel)
        {
            return;
        }

        await channelAdminRightsChecker.CheckAdminRightAsync(
            peer.PeerId,
            userId,
            rights => liveStory ? rights.PostStories : rights.ManageCall,
            RpcErrors.RpcErrors400.ChatAdminRequired);
    }

    private async Task<GroupCallRtmpStreamDocument> GetOrCreateSavedStreamAsync(
        string id,
        long peerId,
        int peerType,
        string defaultRtmpUrl,
        bool revoke,
        string? preferredKey = null,
        int? date = null)
    {
        var saved = await _rtmpStreamCollection.Find(stream => stream.Id == id).FirstOrDefaultAsync();
        if (saved != null && !revoke)
        {
            if (!string.IsNullOrWhiteSpace(preferredKey) && saved.Key != preferredKey)
            {
                var updated = GroupCallRtmpHelper.CreateStreamDocument(
                    id,
                    peerId,
                    peerType,
                    GroupCallRtmpHelper.GetStoredOrDefault(saved.Url, defaultRtmpUrl),
                    preferredKey,
                    saved.Date == 0 ? date : saved.Date);
                await _rtmpStreamCollection.ReplaceOneAsync(
                    existing => existing.Id == id,
                    updated,
                    new ReplaceOptions { IsUpsert = true });

                return updated;
            }

            return saved;
        }

        var rtmpUrl = GroupCallRtmpHelper.GetStoredOrDefault(saved?.Url, defaultRtmpUrl);
        var key = revoke ? null : preferredKey;
        var stream = GroupCallRtmpHelper.CreateStreamDocument(id, peerId, peerType, rtmpUrl, key, date);
        await _rtmpStreamCollection.ReplaceOneAsync(
            existing => existing.Id == id,
            stream,
            new ReplaceOptions { IsUpsert = true });

        return stream;
    }
}
