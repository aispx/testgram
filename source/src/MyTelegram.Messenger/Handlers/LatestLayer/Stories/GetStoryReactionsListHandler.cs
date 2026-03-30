using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Converters;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

internal sealed class GetStoryReactionsListHandler(
    IMongoDatabase mongoDatabase,
    IUserConverterService userConverterService)
    : RpcResultObjectHandler<MyTelegram.Schema.Stories.RequestGetStoryReactionsList, MyTelegram.Schema.Stories.IStoryReactionsList>
{
    private readonly IMongoCollection<BsonDocument> _reactionsCollection =
        mongoDatabase.GetCollection<BsonDocument>("story_reactions");

    protected override async Task<MyTelegram.Schema.Stories.IStoryReactionsList> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Stories.RequestGetStoryReactionsList obj)
    {
        var (peerId, peerType) = StoryHelper.ResolvePeer(obj.Peer, input.UserId);

        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("storyOwnerPeerId", peerId),
            Builders<BsonDocument>.Filter.Eq("storyOwnerPeerType", peerType),
            Builders<BsonDocument>.Filter.Eq("storyId", obj.Id)
        );

        var reactions = await _reactionsCollection.Find(filter).ToListAsync();

        var userIds = reactions.Where(r => r.Contains("userId")).Select(r => r["userId"].AsInt64).Distinct().ToList();
        var userList = await userConverterService.GetUserListAsync(input, userIds, false, false, input.Layer);
        var userDict = userList.ToDictionary(u => u.Id);

        var storyReactions = new TVector<IStoryReaction>();
        foreach (var reaction in reactions)
        {
            if (!reaction.Contains("userId") || !reaction.Contains("reaction"))
                continue;

            var userId = reaction["userId"].AsInt64;
            var reactionStr = reaction["reaction"].AsString;
            var reactionType = reaction.Contains("type") ? reaction["type"].AsString : "emoji";

            IReaction reactionObj;
            if (reactionType == "custom")
            {
                if (long.TryParse(reactionStr, out var documentId))
                {
                    reactionObj = new TReactionCustomEmoji { DocumentId = documentId };
                }
                else
                {
                    continue;
                }
            }
            else
            {
                reactionObj = new TReactionEmoji { Emoticon = reactionStr };
            }

            var storyReaction = new TStoryReaction
            {
                Reaction = reactionObj,
                PeerId = new TPeerUser { UserId = userId },
                Date = reaction.Contains("date") ? reaction["date"].AsInt32 : 0
            };
            storyReactions.Add(storyReaction);
        }

        return new TStoryReactionsList
        {
            Reactions = storyReactions,
            Users = new TVector<IUser>(userList.Cast<IUser>()),
            Chats = new TVector<IChat>()
        };
    }
}