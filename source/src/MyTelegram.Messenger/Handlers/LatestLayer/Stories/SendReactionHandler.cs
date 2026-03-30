using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

internal sealed class SendReactionHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Stories.RequestSendReaction, IUpdates>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");
    private readonly IMongoCollection<BsonDocument> _reactionsCollection =
        mongoDatabase.GetCollection<BsonDocument>("story_reactions");

    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Stories.RequestSendReaction obj)
    {
        var (peerId, peerType) = StoryHelper.ResolvePeer(obj.Peer, input.UserId);

        var filter = Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, peerId),
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, peerType),
            Builders<StoryDocument>.Filter.Eq(s => s.StoryId, obj.StoryId)
        );

        var story = await _storyCollection.Find(filter).FirstOrDefaultAsync();
        if (story == null)
        {
            return new TUpdates
            {
                Updates = new TVector<IUpdate>(),
                Chats = new TVector<IChat>(),
                Users = new TVector<IUser>(),
                Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
        }

        var existingReactionFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("storyOwnerPeerId", peerId),
            Builders<BsonDocument>.Filter.Eq("storyOwnerPeerType", peerType),
            Builders<BsonDocument>.Filter.Eq("storyId", obj.StoryId),
            Builders<BsonDocument>.Filter.Eq("userId", input.UserId)
        );
        var existingReaction = await _reactionsCollection.Find(existingReactionFilter).FirstOrDefaultAsync();

        string? oldReaction = null;
        if (existingReaction != null && existingReaction.Contains("reaction"))
        {
            oldReaction = existingReaction["reaction"].AsString;
        }

        IReaction? sentReaction = null;

        if (obj.Reaction is TReactionEmpty)
        {
            if (existingReaction != null)
            {
                await _reactionsCollection.DeleteOneAsync(existingReactionFilter);
                if (oldReaction != null && story.ReactionsCount > 0)
                {
                    var update = Builders<StoryDocument>.Update.Inc(s => s.ReactionsCount, -1);
                    await _storyCollection.UpdateOneAsync(filter, update);
                }
            }
            sentReaction = new TReactionEmpty();
        }
        else
        {
            var newReactionDoc = new BsonDocument
            {
                { "storyOwnerPeerId", peerId },
                { "storyOwnerPeerType", peerType },
                { "storyId", obj.StoryId },
                { "userId", input.UserId },
                { "date", DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
            };

            if (obj.Reaction is TReactionEmoji emoji)
            {
                newReactionDoc["reaction"] = emoji.Emoticon;
                newReactionDoc["type"] = "emoji";
                sentReaction = obj.Reaction;
            }
            else if (obj.Reaction is TReactionCustomEmoji customEmoji)
            {
                newReactionDoc["reaction"] = customEmoji.DocumentId.ToString();
                newReactionDoc["type"] = "custom";
                sentReaction = obj.Reaction;
            }
            else
            {
                sentReaction = obj.Reaction;
            }

            await _reactionsCollection.ReplaceOneAsync(existingReactionFilter, newReactionDoc, new ReplaceOptions { IsUpsert = true });

            if (oldReaction == null)
            {
                var update = Builders<StoryDocument>.Update.Inc(s => s.ReactionsCount, 1);
                await _storyCollection.UpdateOneAsync(filter, update);
            }
        }

        return new TUpdates
        {
            Updates = new TVector<IUpdate>
            {
                new TUpdateSentStoryReaction
                {
                    Peer = StoryHelper.CreatePeer(peerType, peerId),
                    StoryId = obj.StoryId,
                    Reaction = sentReaction ?? new TReactionEmpty()
                }
            },
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Seq = 0
        };
    }
}
