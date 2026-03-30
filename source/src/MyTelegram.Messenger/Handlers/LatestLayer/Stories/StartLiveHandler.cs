using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;
internal sealed class StartLiveHandler(
    IIdGenerator idGenerator,
    IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<RequestStartLive, IUpdates>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");
    private readonly IMongoCollection<BsonDocument> _groupCallCollection =
        mongoDatabase.GetCollection<BsonDocument>("group_calls");

    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestStartLive obj)
    {
        var (ownerPeerId, ownerPeerType) = StoryHelper.ResolvePeer(obj.Peer, input.UserId);

        var currentDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var period = 86400;
        long expireDate = currentDate + period;

        var storyId = await idGenerator.NextIdAsync(IdType.StoryId, input.UserId);
        var groupCallId = await idGenerator.NextIdAsync(IdType.MessageId, input.UserId);
        var groupCallAccessHash = (long)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var rtmpUrl = "rtmp://testgram.b2t.pro:1935/live";
        var streamKey = Guid.NewGuid().ToString("N");

        var groupCallDoc = new BsonDocument
        {
            { "_id", groupCallId },
            { "access_hash", groupCallAccessHash },
            { "peer_id", ownerPeerId },
            { "peer_type", ownerPeerType },
            { "is_live_story", true },
            { "rtmp_stream", obj.RtmpStream },
            { "messages_enabled", obj.MessagesEnabled ?? true },
            { "rtmp_url", rtmpUrl },
            { "rtmp_stream_key", streamKey },
            { "creator_id", input.UserId },
            { "participants_count", 0 },
            { "created_at", currentDate }
        };
        await _groupCallCollection.InsertOneAsync(groupCallDoc);

        var isCloseFriends = obj.PrivacyRules?.Any(p => p is TInputPrivacyValueAllowCloseFriends) ?? false;

        var storyDocument = new StoryDocument
        {
            Id = ObjectId.GenerateNewId(),
            OwnerPeerId = ownerPeerId,
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
            MessagesEnabled = obj.MessagesEnabled ?? true,
            SendPaidMessagesStars = obj.SendPaidMessagesStars ?? 0,
            GroupCallId = groupCallId,
            GroupCallAccessHash = groupCallAccessHash,
            RtmpUrl = rtmpUrl,
            RtmpStreamKey = streamKey
        };

        await _storyCollection.InsertOneAsync(storyDocument);

        var peer = StoryHelper.CreatePeer(ownerPeerType, ownerPeerId);
        
        var groupCall = new TGroupCall
        {
            Id = groupCallId,
            AccessHash = groupCallAccessHash,
            ParticipantsCount = 0,
            RtmpStream = obj.RtmpStream,
            MessagesEnabled = obj.MessagesEnabled ?? true,
            Creator = true,
            Version = 0,
            UnmutedVideoLimit = 0
        };

        var storyItem = new TStoryItemSkipped
        {
            Id = storyId,
            Date = (int)currentDate,
            ExpireDate = (int)expireDate,
            Live = true,
            CloseFriends = isCloseFriends
        };

        return new TUpdates
        {
            Updates = new TVector<IUpdate>
            {
                new TUpdateGroupCall
                {
                    LiveStory = true,
                    Peer = peer,
                    Call = groupCall
                },
                new TUpdateStoryID { Id = storyId, RandomId = obj.RandomId },
                new TUpdateStory
                {
                    Peer = peer,
                    Story = storyItem
                }
            },
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Date = (int)currentDate
        };
    }
}
