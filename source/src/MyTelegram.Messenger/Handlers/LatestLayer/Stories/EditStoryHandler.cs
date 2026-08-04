using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

/// <summary>
/// Edit an uploaded <a href="https://corefork.telegram.org/api/stories">story</a>.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 STORY_ID_EMPTY You specified no story IDs.
/// 400 STORY_NOT_MODIFIED The new story information you passed is equal to the previous story information, thus it wasn't modified.
/// <para><c>See <a href="https://corefork.telegram.org/method/stories.editStory"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class EditStoryHandler(
    IMongoDatabase mongoDatabase,
    IUserAppService userAppService,
    ITokenizer tokenizer,
    IStoryAccessService storyAccessService,
    IStoryConfigProvider storyConfigProvider,
    IStoryMediaService storyMediaService,
    IStoryUpdatesSender storyUpdatesSender)
    : RpcResultObjectHandler<RequestEditStory, IUpdates>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");

    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestEditStory obj)
    {
        var (ownerPeerId, ownerPeerType) =
            await storyAccessService.ResolveOwnedPeerAsync(obj.Peer, input.UserId, StoryRight.Edit);

        if (obj.Id <= 0)
        {
            RpcErrors.RpcErrors400.StoryIdEmpty.ThrowRpcError();
        }

        var filter = Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, ownerPeerId),
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, ownerPeerType),
            Builders<StoryDocument>.Filter.Eq(s => s.StoryId, obj.Id),
            Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false)
        );

        var story = await _storyCollection.Find(filter).FirstOrDefaultAsync();
        if (story == null)
        {
            RpcErrors.RpcErrors400.StoryIdInvalid.ThrowRpcError();
        }

        var updates = new List<UpdateDefinition<StoryDocument>>();

        if (obj.Caption != null)
        {
            var userReadModel = await userAppService.GetAsync((long?)input.UserId);
            var isPremium = userReadModel?.Premium ?? false;

            if (obj.Caption.Length > storyConfigProvider.GetCaptionLengthLimit(isPremium))
            {
                RpcErrors.RpcErrors400.MediaCaptionTooLong.ThrowRpcError();
            }

            var hashtags = StoryHelper.ExtractHashtags(obj.Caption);

            updates.Add(Builders<StoryDocument>.Update.Set(s => s.Caption, obj.Caption));
            updates.Add(Builders<StoryDocument>.Update.Set(s => s.Hashtags, hashtags));
            updates.Add(Builders<StoryDocument>.Update.Set(
                s => s.HashtagTokens,
                hashtags.Count > 0 ? tokenizer.BuildSearchTokens(string.Join(' ', hashtags)) ?? [] : []));
        }

        if (obj.Media != null)
        {
            // Resolve the media the same way sendStory does: storing the upload's InputFile.Id would
            // leave the story pointing at an id that cannot be downloaded.
            var media = await storyMediaService.SaveStoryMediaAsync(obj.Media);

            updates.Add(Builders<StoryDocument>.Update.Set(s => s.MediaType, media.MediaType));
            updates.Add(Builders<StoryDocument>.Update.Set(s => s.MediaFileId, media.FileId));
            updates.Add(Builders<StoryDocument>.Update.Set(s => s.MediaAccessHash, media.AccessHash));
            updates.Add(Builders<StoryDocument>.Update.Set(s => s.MediaFileReference, media.FileReference));
            updates.Add(Builders<StoryDocument>.Update.Set(s => s.MediaDcId, media.DcId));
            updates.Add(Builders<StoryDocument>.Update.Set(s => s.MediaSize, media.Size));
            updates.Add(Builders<StoryDocument>.Update.Set(s => s.MediaMimeType, media.MimeType));
            updates.Add(Builders<StoryDocument>.Update.Set(s => s.VideoWidth, media.VideoWidth));
            updates.Add(Builders<StoryDocument>.Update.Set(s => s.VideoHeight, media.VideoHeight));
            updates.Add(Builders<StoryDocument>.Update.Set(s => s.VideoDuration, media.VideoDuration));
            updates.Add(Builders<StoryDocument>.Update.Set(s => s.VideoThumbBytes, media.VideoThumbBytes));
        }

        if (obj.Entities != null)
        {
            updates.Add(Builders<StoryDocument>.Update.Set(
                s => s.Entities, StoryHelper.SerializeEntities(obj.Entities)));
        }

        if (obj.MediaAreas != null)
        {
            updates.Add(Builders<StoryDocument>.Update.Set(
                s => s.MediaAreas, StoryMediaAreaHelper.Parse(obj.MediaAreas)));
        }

        if (obj.PrivacyRules is { Count: > 0 })
        {
            var privacyRules = StoryHelper.ParsePrivacyRules(obj.PrivacyRules);
            updates.Add(Builders<StoryDocument>.Update.Set(s => s.PrivacyRules, privacyRules));
            updates.Add(Builders<StoryDocument>.Update.Set(
                s => s.CloseFriends,
                privacyRules.Any(r => r.Type == StoryPrivacyRuleType.AllowCloseFriends)));
        }

        if (obj.Music is TInputDocument music)
        {
            updates.Add(Builders<StoryDocument>.Update.Set(s => s.MusicDocumentId, music.Id));
            updates.Add(Builders<StoryDocument>.Update.Set(s => s.MusicAccessHash, music.AccessHash));
        }

        if (updates.Count == 0)
        {
            RpcErrors.RpcErrors400.StoryNotModified.ThrowRpcError();
        }

        updates.Add(Builders<StoryDocument>.Update.Set(s => s.Edited, true));

        await _storyCollection.UpdateOneAsync(filter, Builders<StoryDocument>.Update.Combine(updates));

        var updatedStory = await _storyCollection.Find(filter).FirstOrDefaultAsync() ?? story!;

        var peer = StoryHelper.CreatePeer(ownerPeerType, ownerPeerId);

        await storyUpdatesSender.PushStoryUpdateAsync(
            updatedStory,
            new TUpdates
            {
                Updates = new TVector<IUpdate>
                {
                    new TUpdateStory
                    {
                        Peer = peer,
                        Story = StoryHelper.ConvertToStoryItem(updatedStory)
                    }
                },
                Chats = new TVector<IChat>(),
                Users = new TVector<IUser>(),
                Date = CurrentDate
            },
            excludeUserId: input.UserId);

        return new TUpdates
        {
            Updates = new TVector<IUpdate>
            {
                new TUpdateStory
                {
                    Peer = peer,
                    Story = StoryHelper.ConvertToStoryItem(updatedStory, input.UserId, includePrivacy: true)
                }
            },
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Date = CurrentDate
        };
    }
}
