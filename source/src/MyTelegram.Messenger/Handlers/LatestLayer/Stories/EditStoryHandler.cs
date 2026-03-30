using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

internal sealed class EditStoryHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<RequestEditStory, IUpdates>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");

    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestEditStory obj)
    {
        var (ownerPeerId, ownerPeerType) = StoryHelper.ResolvePeer(obj.Peer, input.UserId);

        var filter = Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, ownerPeerId),
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, ownerPeerType),
            Builders<StoryDocument>.Filter.Eq(s => s.StoryId, obj.Id),
            Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false)
        );

        var updateDef = new List<UpdateDefinition<StoryDocument>>();

        if (obj.Caption != null)
        {
            updateDef.Add(Builders<StoryDocument>.Update.Set(s => s.Caption, obj.Caption));
        }

        if (obj.Media != null)
        {
            if (obj.Media is TInputMediaUploadedPhoto photoMedia && photoMedia.File is TInputFile photoFile)
            {
                updateDef.Add(Builders<StoryDocument>.Update.Set(s => s.MediaType, 1));
                updateDef.Add(Builders<StoryDocument>.Update.Set(s => s.MediaFileId, photoFile.Id));
            }
            else if (obj.Media is TInputMediaUploadedDocument docMedia && docMedia.File is TInputFile docFile)
            {
                updateDef.Add(Builders<StoryDocument>.Update.Set(s => s.MediaType, 2));
                updateDef.Add(Builders<StoryDocument>.Update.Set(s => s.MediaFileId, docFile.Id));
            }
        }

        if (obj.Entities != null && obj.Entities.Count > 0)
        {
            var entitiesData = obj.Entities.Select(e =>
            {
                var data = new Dictionary<string, object>
                {
                    { "constructorId", e.ConstructorId },
                    { "offset", e.Offset },
                    { "length", e.Length }
                };

                if (e is TMessageEntityTextUrl textUrlEntity)
                    data["url"] = textUrlEntity.Url;
                else if (e is TMessageEntityMentionName mentionNameEntity)
                    data["userId"] = mentionNameEntity.UserId;
                else if (e is TMessageEntityPre preEntity)
                {
                    data["language"] = preEntity.Language;
                }

                return data;
            }).ToList();

            var entitiesJson = System.Text.Json.JsonSerializer.Serialize(entitiesData);
            updateDef.Add(Builders<StoryDocument>.Update.Set(s => s.Entities, entitiesJson));
        }

        if (obj.PrivacyRules != null && obj.PrivacyRules.Count > 0)
        {
            var privacyRules = obj.PrivacyRules.Select(pr =>
            {
                int type = 0;
                long? userId = null;
                long? chatId = null;

                if (pr is TInputPrivacyValueAllowAll) type = 0;
                else if (pr is TInputPrivacyValueAllowContacts) type = 1;
                else if (pr is TInputPrivacyValueAllowCloseFriends) type = 4;
                else if (pr is TInputPrivacyValueDisallowAll) type = 2;
                else if (pr is TInputPrivacyValueDisallowContacts) type = 3;
                else if (pr is TInputPrivacyValueDisallowUsers privacyDisallowUsers)
                {
                    type = 5;
                    var users = privacyDisallowUsers.Users;
                    if (users != null && users.Count > 0 && users[0] is TInputUser tInputUser)
                        userId = tInputUser.UserId;
                }
                else if (pr is TInputPrivacyValueAllowUsers privacyAllowUsers)
                {
                    type = 6;
                    var users = privacyAllowUsers.Users;
                    if (users != null && users.Count > 0 && users[0] is TInputUser tInputUser)
                        userId = tInputUser.UserId;
                }

                return new StoryPrivacyRule
                {
                    Type = type,
                    UserId = userId,
                    ChatId = chatId
                };
            }).ToList();

            updateDef.Add(Builders<StoryDocument>.Update.Set(s => s.PrivacyRules, privacyRules));
        }

        IStoryItem? updatedStoryItem = null;

        if (updateDef.Count > 0)
        {
            var update = Builders<StoryDocument>.Update.Combine(updateDef);
            await _storyCollection.UpdateOneAsync(filter, update);

            var updatedStory = await _storyCollection.Find(filter).FirstOrDefaultAsync();
            if (updatedStory != null)
            {
                updatedStoryItem = StoryHelper.ConvertToStoryItem(updatedStory, input.UserId);
            }
        }
        else
        {
            var existingStory = await _storyCollection.Find(filter).FirstOrDefaultAsync();
            if (existingStory != null)
            {
                updatedStoryItem = StoryHelper.ConvertToStoryItem(existingStory, input.UserId);
            }
        }

        var updates = new List<IUpdate>();
        if (updatedStoryItem != null)
        {
            var updateStory = new TUpdateStory
            {
                Peer = StoryHelper.CreatePeer(ownerPeerType, ownerPeerId),
                Story = updatedStoryItem
            };
            updates.Add(updateStory);
        }

        return new TUpdates
        {
            Updates = new TVector<IUpdate>(updates),
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Date = CurrentDate
        };
    }
}
