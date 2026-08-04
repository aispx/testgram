using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Converters;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

/// <summary>
/// Obtain the list of users that viewed a specific <a href="https://corefork.telegram.org/api/stories">story we posted</a>.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 STORY_ID_INVALID The specified story ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stories.getStoryViewsList"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// <para>
/// The viewer list is the poster's private data, so this requires the right to manage the peer's stories.
/// </para>
/// </remarks>
internal sealed class GetStoryViewsListHandler(
    IMongoDatabase mongoDatabase,
    IUserConverterService userConverterService,
    IQueryProcessor queryProcessor,
    IStoryAccessService storyAccessService)
    : RpcResultObjectHandler<RequestGetStoryViewsList, IStoryViewsList>
{
    private const int MaxLimit = 100;
    private const int DefaultLimit = 20;

    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");
    private readonly IMongoCollection<BsonDocument> _storyViewsCollection =
        mongoDatabase.GetCollection<BsonDocument>("story_views");
    private readonly IMongoCollection<BsonDocument> _reactionsCollection =
        mongoDatabase.GetCollection<BsonDocument>("story_reactions");

    protected override async Task<IStoryViewsList> HandleCoreAsync(IRequestInput input, RequestGetStoryViewsList obj)
    {
        var (peerId, peerType) =
            await storyAccessService.ResolveOwnedPeerAsync(obj.Peer, input.UserId, StoryRight.Edit);

        var story = await _storyCollection
            .Find(Builders<StoryDocument>.Filter.And(
                Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, peerId),
                Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, peerType),
                Builders<StoryDocument>.Filter.Eq(s => s.StoryId, obj.Id),
                Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false)))
            .FirstOrDefaultAsync();

        if (story == null)
        {
            RpcErrors.RpcErrors400.StoryIdInvalid.ThrowRpcError();
        }

        var views = await _storyViewsCollection
            .Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("ownerPeerId", peerId),
                Builders<BsonDocument>.Filter.Eq("ownerPeerType", peerType),
                Builders<BsonDocument>.Filter.Eq("storyId", obj.Id)))
            .ToListAsync();

        var reactions = await LoadReactionsAsync(peerId, peerType, obj.Id);

        var entries = views
            .Where(v => v.Contains("viewerUserId"))
            .Select(v => new ViewEntry(
                v["viewerUserId"].AsInt64,
                v.Contains("date") ? v["date"].AsInt32 : 0))
            .GroupBy(e => e.UserId)
            .Select(g => g.OrderByDescending(e => e.Date).First())
            .ToList();

        if (obj.JustContacts)
        {
            var contactIds = await queryProcessor.ProcessAsync(new GetContactUserIdListQuery(input.UserId));
            var contactSet = contactIds.ToHashSet();
            entries = entries.Where(e => contactSet.Contains(e.UserId)).ToList();
        }

        // Load the user objects before applying the text filter, which matches on names.
        var allUsers = await userConverterService.GetUserListAsync(
            input, entries.Select(e => e.UserId).ToList(), false, false, input.Layer);
        var usersById = allUsers.ToDictionary(u => u.Id);

        if (!string.IsNullOrWhiteSpace(obj.Q))
        {
            entries = entries
                .Where(e => usersById.TryGetValue(e.UserId, out var user) && MatchesQuery(user, obj.Q))
                .ToList();
        }

        entries = Sort(entries, reactions, obj.ReactionsFirst).ToList();

        var totalFiltered = entries.Count;
        var offset = ParseOffset(obj.Offset);
        var limit = obj.Limit > 0 ? Math.Min(obj.Limit, MaxLimit) : DefaultLimit;
        var page = entries.Skip(offset).Take(limit).ToList();

        var storyViews = new TVector<IStoryView>();
        var pageUsers = new TVector<IUser>();

        foreach (var entry in page)
        {
            reactions.TryGetValue(entry.UserId, out var reaction);

            storyViews.Add(new TStoryView
            {
                UserId = entry.UserId,
                Date = entry.Date,
                Reaction = reaction
            });

            if (usersById.TryGetValue(entry.UserId, out var user))
            {
                pageUsers.Add((IUser)user);
            }
        }

        var consumed = offset + page.Count;

        return new TStoryViewsList
        {
            Count = totalFiltered,
            ViewsCount = story!.ViewsCount,
            ForwardsCount = story.ForwardsCount,
            ReactionsCount = story.ReactionsCount,
            Views = storyViews,
            Users = pageUsers,
            Chats = new TVector<IChat>(),
            NextOffset = consumed < totalFiltered ? consumed.ToString() : null
        };
    }

    private async Task<Dictionary<long, IReaction>> LoadReactionsAsync(long peerId, int peerType, int storyId)
    {
        var docs = await _reactionsCollection
            .Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("storyOwnerPeerId", peerId),
                Builders<BsonDocument>.Filter.Eq("storyOwnerPeerType", peerType),
                Builders<BsonDocument>.Filter.Eq("storyId", storyId)))
            .ToListAsync();

        var result = new Dictionary<long, IReaction>();

        foreach (var doc in docs)
        {
            if (!doc.Contains("userId"))
            {
                continue;
            }

            var reaction = StoryResponseBuilder.ToReaction(doc);
            if (reaction != null)
            {
                result[doc["userId"].AsInt64] = reaction;
            }
        }

        return result;
    }

    /// <summary>
    /// Viewers who reacted float to the top when <c>reactions_first</c> is set; otherwise the list is
    /// simply newest-view-first.
    /// </summary>
    private static IEnumerable<ViewEntry> Sort(
        List<ViewEntry> entries,
        Dictionary<long, IReaction> reactions,
        bool reactionsFirst)
    {
        return reactionsFirst
            ? entries
                .OrderByDescending(e => reactions.ContainsKey(e.UserId))
                .ThenByDescending(e => e.Date)
            : entries.OrderByDescending(e => e.Date);
    }

    private static bool MatchesQuery(ILayeredUser user, string query)
    {
        if (user is not TUser tUser)
        {
            return false;
        }

        return Contains(tUser.FirstName) || Contains(tUser.LastName) || Contains(tUser.Username);

        bool Contains(string? value) =>
            !string.IsNullOrEmpty(value) && value.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static int ParseOffset(string? offset)
    {
        return int.TryParse(offset, out var parsed) && parsed > 0 ? parsed : 0;
    }

    private sealed record ViewEntry(long UserId, int Date);
}
