using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Converters;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

/// <summary>
/// Get the <a href="https://corefork.telegram.org/api/reactions">reaction</a> and interaction list of a
/// <a href="https://corefork.telegram.org/api/stories">story</a> posted to a channel.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 STORY_ID_INVALID The specified story ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stories.getStoryReactionsList"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetStoryReactionsListHandler(
    IMongoDatabase mongoDatabase,
    IUserConverterService userConverterService,
    IStoryAccessService storyAccessService)
    : RpcResultObjectHandler<MyTelegram.Schema.Stories.RequestGetStoryReactionsList,
        MyTelegram.Schema.Stories.IStoryReactionsList>
{
    private const int MaxLimit = 100;
    private const int DefaultLimit = 20;

    private readonly IMongoCollection<BsonDocument> _reactionsCollection =
        mongoDatabase.GetCollection<BsonDocument>("story_reactions");

    protected override async Task<MyTelegram.Schema.Stories.IStoryReactionsList> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Stories.RequestGetStoryReactionsList obj)
    {
        var (peerId, peerType) =
            await storyAccessService.ResolveOwnedPeerAsync(obj.Peer, input.UserId, StoryRight.Edit);

        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("storyOwnerPeerId", peerId),
            Builders<BsonDocument>.Filter.Eq("storyOwnerPeerType", peerType),
            Builders<BsonDocument>.Filter.Eq("storyId", obj.Id)
        );

        if (TryDescribeReaction(obj.Reaction, out var reactionValue, out var reactionType))
        {
            filter = Builders<BsonDocument>.Filter.And(
                filter,
                Builders<BsonDocument>.Filter.Eq("reaction", reactionValue),
                Builders<BsonDocument>.Filter.Eq("type", reactionType));
        }

        var docs = await _reactionsCollection.Find(filter).ToListAsync();

        var entries = new List<ReactionEntry>();
        foreach (var doc in docs)
        {
            if (!doc.Contains("userId"))
            {
                continue;
            }

            var reaction = StoryResponseBuilder.ToReaction(doc);
            if (reaction == null)
            {
                continue;
            }

            entries.Add(new ReactionEntry(
                doc["userId"].AsInt64,
                doc.Contains("date") ? doc["date"].AsInt32 : 0,
                reaction));
        }

        entries = entries.OrderByDescending(e => e.Date).ToList();

        var totalCount = entries.Count;
        var offset = int.TryParse(obj.Offset, out var parsedOffset) && parsedOffset > 0 ? parsedOffset : 0;
        var limit = obj.Limit > 0 ? Math.Min(obj.Limit, MaxLimit) : DefaultLimit;
        var page = entries.Skip(offset).Take(limit).ToList();

        var userList = await userConverterService.GetUserListAsync(
            input, page.Select(e => e.UserId).Distinct().ToList(), false, false, input.Layer);

        var storyReactions = new TVector<IStoryReaction>();
        foreach (var entry in page)
        {
            storyReactions.Add(new TStoryReaction
            {
                Reaction = entry.Reaction,
                PeerId = new TPeerUser { UserId = entry.UserId },
                Date = entry.Date
            });
        }

        var consumed = offset + page.Count;

        return new TStoryReactionsList
        {
            Count = totalCount,
            Reactions = storyReactions,
            Users = new TVector<IUser>(userList.Cast<IUser>()),
            Chats = new TVector<IChat>(),
            NextOffset = consumed < totalCount ? consumed.ToString() : null
        };
    }

    /// <summary>
    /// Describes a requested reaction filter the same way <c>story_reactions</c> stores it.
    /// </summary>
    private static bool TryDescribeReaction(IReaction? reaction, out string value, out string type)
    {
        switch (reaction)
        {
            case TReactionEmoji emoji:
                value = emoji.Emoticon;
                type = "emoji";
                return true;
            case TReactionCustomEmoji customEmoji:
                value = customEmoji.DocumentId.ToString();
                type = "custom";
                return true;
            default:
                value = string.Empty;
                type = string.Empty;
                return false;
        }
    }

    private sealed record ReactionEntry(long UserId, int Date, IReaction Reaction);
}
