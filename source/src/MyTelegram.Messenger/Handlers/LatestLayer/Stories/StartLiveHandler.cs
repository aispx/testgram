using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;
internal sealed class StartLiveHandler(
    IIdGenerator idGenerator,
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options)
    : RpcResultObjectHandler<RequestStartLive, IUpdates>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");
    private readonly IMongoCollection<GroupCallRtmpStreamDocument> _rtmpStreamCollection =
        mongoDatabase.GetCollection<GroupCallRtmpStreamDocument>("group_call_rtmp_streams");

    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestStartLive obj)
    {
        var ownerPeer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (ownerPeer == null)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            return null!;
        }

        var ownerPeerType = StoryHelper.ToStoryPeerType(ownerPeer.PeerType);
        if (ownerPeerType < 0)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            return null!;
        }

        var currentDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var period = 86400;
        long expireDate = currentDate + period;

        var existingStory = await _storyCollection.Find(s =>
                s.OwnerPeerId == ownerPeer.PeerId &&
                s.OwnerPeerType == ownerPeerType &&
                s.RandomId == obj.RandomId &&
                s.IsLive &&
                !s.Deleted)
            .FirstOrDefaultAsync();
        if (existingStory != null)
        {
            var existingCall = await _groupCallCollection
                .Find(call => call.CallId == existingStory.GroupCallId)
                .FirstOrDefaultAsync();
            if (existingCall != null)
            {
                return BuildUpdates(existingCall, existingStory, obj.RandomId);
            }
        }

        var storyId = await idGenerator.NextIdAsync(IdType.StoryId, input.UserId);
        var groupCallId = await idGenerator.NextIdAsync(IdType.MessageId, input.UserId);
        var groupCallAccessHash = GroupCallStateHelper.CreateAccessHash();
        var streamId = GroupCallRtmpHelper.GetStreamId(ownerPeer.PeerId, (int)ownerPeer.PeerType, liveStory: true);
        var savedRtmpStream = obj.RtmpStream
            ? await _rtmpStreamCollection.Find(stream => stream.Id == streamId).FirstOrDefaultAsync()
            : null;
        if (obj.RtmpStream && savedRtmpStream == null)
        {
            savedRtmpStream = GroupCallRtmpHelper.CreateStreamDocument(
                streamId,
                ownerPeer.PeerId,
                (int)ownerPeer.PeerType,
                GroupCallRtmpHelper.GetRtmpStreamUrl(options.CurrentValue),
                date: (int)currentDate);
            await _rtmpStreamCollection.ReplaceOneAsync(
                stream => stream.Id == streamId,
                savedRtmpStream,
                new ReplaceOptions { IsUpsert = true });
        }

        var rtmpUrl = obj.RtmpStream
            ? GroupCallRtmpHelper.GetStoredOrDefault(
                savedRtmpStream?.Url,
                GroupCallRtmpHelper.GetRtmpStreamUrl(options.CurrentValue))
            : null;
        var streamKey = obj.RtmpStream
            ? savedRtmpStream?.Key
            : null;

        var isCloseFriends = obj.PrivacyRules?.Any(p => p is TInputPrivacyValueAllowCloseFriends) ?? false;
        var messagesEnabled = obj.MessagesEnabled ?? true;

        var storyDocument = new StoryDocument
        {
            Id = ObjectId.GenerateNewId(),
            OwnerPeerId = ownerPeer.PeerId,
            OwnerPeerType = ownerPeerType,
            StoryId = storyId,
            Date = currentDate,
            ExpireDate = expireDate,
            Caption = obj.Caption,
            Pinned = obj.Pinned,
            NoForwards = obj.Noforwards,
            MediaDcId = 2,
            ViewsCount = 0,
            ForwardsCount = 0,
            ReactionsCount = 0,
            Period = (int)period,
            IsLive = true,
            CloseFriends = isCloseFriends,
            RtmpStream = obj.RtmpStream,
            MessagesEnabled = messagesEnabled,
            SendPaidMessagesStars = obj.SendPaidMessagesStars ?? 0,
            GroupCallId = groupCallId,
            GroupCallAccessHash = groupCallAccessHash,
            RtmpUrl = rtmpUrl,
            RtmpStreamKey = streamKey,
            RandomId = obj.RandomId
        };

        var groupCallDoc = new GroupCallDocument
        {
            Id = groupCallId,
            CallId = groupCallId,
            AccessHash = groupCallAccessHash,
            RandomId = unchecked((int)obj.RandomId),
            PeerId = ownerPeer.PeerId,
            PeerType = (int)ownerPeer.PeerType,
            CreatorId = input.UserId,
            RtmpStream = obj.RtmpStream,
            RtmpUrl = rtmpUrl,
            RtmpStreamKey = streamKey,
            LiveStory = true,
            StoryId = storyId,
            MessagesEnabled = messagesEnabled,
            SendPaidMessagesStars = obj.SendPaidMessagesStars,
            Version = 1,
            Date = (int)currentDate
        };

        await _groupCallCollection.InsertOneAsync(groupCallDoc);
        await _storyCollection.InsertOneAsync(storyDocument);

        return BuildUpdates(groupCallDoc, storyDocument, obj.RandomId);
    }

    private IUpdates BuildUpdates(GroupCallDocument groupCall, StoryDocument storyDocument, long randomId)
    {
        var storyItem = new TStoryItemSkipped
        {
            Id = storyDocument.StoryId,
            Date = (int)storyDocument.Date,
            ExpireDate = (int)storyDocument.ExpireDate,
            Live = true,
            CloseFriends = storyDocument.CloseFriends
        };

        return GroupCallStateHelper.Updates(
            new TUpdateGroupCall
            {
                LiveStory = true,
                Peer = StoryHelper.CreatePeer(storyDocument.OwnerPeerType, storyDocument.OwnerPeerId),
                Call = GroupCallStateHelper.ToGroupCall(groupCall, groupCall.CreatorId)
            },
            new TUpdateStoryID { Id = storyDocument.StoryId, RandomId = randomId },
            new TUpdateStory
            {
                Peer = StoryHelper.CreatePeer(storyDocument.OwnerPeerType, storyDocument.OwnerPeerId),
                Story = storyItem
            });
    }
}
