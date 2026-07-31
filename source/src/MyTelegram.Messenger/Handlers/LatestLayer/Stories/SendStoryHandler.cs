using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services;
using MyTelegram.Messenger.Services.Stats;
using MyTelegram.Messenger.Services.Stats.Ingestion;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

internal sealed class SendStoryHandler(
    IIdGenerator idGenerator,
    IMongoDatabase mongoDatabase,
    IMediaHelper mediaHelper,
    IMetricsStore metricsStore)
    : RpcResultObjectHandler<RequestSendStory, IUpdates>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");
    private readonly IMongoCollection<BsonDocument> _channelMembersCollection =
        mongoDatabase.GetCollection<BsonDocument>("channel_members");

    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestSendStory obj)
    {
        var (ownerPeerId, ownerPeerType) = StoryHelper.ResolvePeer(obj.Peer, input.UserId);

        if (ownerPeerType == 2)
        {
            var memberFilter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("channelId", ownerPeerId),
                Builders<BsonDocument>.Filter.Eq("userId", input.UserId),
                Builders<BsonDocument>.Filter.Eq("isMember", true)
            );
            var member = await _channelMembersCollection.Find(memberFilter).FirstOrDefaultAsync();
            if (member == null)
            {
                RpcErrors.RpcErrors400.ChatAdminRequired.ThrowRpcError();
            }
        }

        var currentDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var period = obj.Period ?? 86400;
        long expireDate = currentDate + period;

        int storyId;
        if (obj.RandomId != 0)
        {
            var existingStory = await _storyCollection.Find(s => s.RandomId == obj.RandomId).FirstOrDefaultAsync();
            if (existingStory != null)
            {
                storyId = existingStory.StoryId;
            }
            else
            {
                storyId = await idGenerator.NextIdAsync(IdType.StoryId, input.UserId);
            }
        }
        else
        {
            storyId = await idGenerator.NextIdAsync(IdType.StoryId, input.UserId);
        }

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
            RandomId = obj.RandomId,
            Period = period,
            ViewsCount = 0,
            ForwardsCount = 0,
            ReactionsCount = 0
        };

        if (obj.Media != null)
        {
            var savedMedia = await mediaHelper.SaveMediaAsync(obj.Media);

            if (savedMedia == null)
            {
                RpcErrors.RpcErrors400.MediaEmpty.ThrowRpcError();
            }

            if (obj.Media is TInputMediaUploadedPhoto photoMedia && photoMedia.File is TInputFile photoFile)
            {
                storyDocument.MediaType = 1;

                if (savedMedia is TMessageMediaPhoto photoMsg && photoMsg.Photo is TPhoto photo)
                {
                    storyDocument.MediaFileId = photo.Id;
                    storyDocument.MediaAccessHash = photo.AccessHash;
                    storyDocument.MediaDcId = photo.DcId;
                    storyDocument.MediaFileReference = photo.FileReference.Length > 0 ? photo.FileReference.ToArray() : [];
                }
                else
                {
                    throw new Exception("Failed to save photo to file server");
                }
            }
            else if (obj.Media is TInputMediaUploadedDocument docMedia && docMedia.File is TInputFile docFile)
            {
                storyDocument.MediaType = 2;

                if (savedMedia is TMessageMediaDocument docMsg && docMsg.Document is TDocument doc)
                {
                    storyDocument.MediaFileId = doc.Id;
                    storyDocument.MediaAccessHash = doc.AccessHash;
                    storyDocument.MediaDcId = doc.DcId;
                    storyDocument.MediaFileReference = doc.FileReference.Length > 0 ? doc.FileReference.ToArray() : [];
                    storyDocument.MediaSize = doc.Size;
                    storyDocument.MediaMimeType = doc.MimeType;
                }
                else
                {
                    throw new Exception("Failed to save document/video to file server");
                }
                
                if (docMedia.Attributes != null)
                {
                    foreach (var attr in docMedia.Attributes)
                    {
                        if (attr is TDocumentAttributeVideo videoAttr)
                        {
                            storyDocument.VideoWidth = videoAttr.W;
                            storyDocument.VideoHeight = videoAttr.H;
                            storyDocument.VideoDuration = (int)videoAttr.Duration;
                        }
                    }
                }
            }
        }

        await _storyCollection.InsertOneAsync(storyDocument);

        // Stats ingestion: the story's post date (gauge) enables newest-first ordering in
        // recent-post interactions and marks the story entity as existing for stats.getStoryStats;
        // the channel-level story count is the denominator of the per-story means.
        var storyPostTime = (int)storyDocument.Date;
        var storyUtcDay = StatsIngestionTime.ToUtcDayOrNow(storyPostTime);
        await metricsStore.RecordAsync(
            new StatsEntityKey(StatsEntityType.Story, ownerPeerId, storyId), StatsMetricNames.PostDate,
            storyUtcDay, storyPostTime > 0 ? storyPostTime : StatsIngestionTime.CurrentUnixTime());
        if (ownerPeerType == StoryHelper.PeerTypeChannel)
        {
            await metricsStore.RecordAsync(
                new StatsEntityKey(StatsEntityType.Channel, ownerPeerId, 0), StatsMetricNames.StoryPosts, storyUtcDay, 1);
        }

        var storyItem = StoryHelper.ConvertToStoryItem(storyDocument, input.UserId);

        var updateStoryId = new TUpdateStoryID
        {
            Id = storyId,
            RandomId = obj.RandomId
        };

        var updateStory = new TUpdateStory
        {
            Peer = StoryHelper.CreatePeer(ownerPeerType, ownerPeerId),
            Story = storyItem
        };

        return new TUpdates
        {
            Updates = new TVector<IUpdate> { updateStoryId, updateStory },
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Date = CurrentDate
        };
    }
}
