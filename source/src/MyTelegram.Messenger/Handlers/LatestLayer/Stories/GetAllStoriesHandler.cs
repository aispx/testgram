using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

/// <summary>
/// Fetch the List of active (or hidden) stories, see <a href="https://corefork.telegram.org/api/stories#watching-stories">here »</a> for more info on watching stories.
/// <para><c>See <a href="https://corefork.telegram.org/method/stories.getAllStories"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// <para>
/// Only stories the caller could actually see are returned: those of their contacts and of channels they
/// are in, minus anything the story's privacy rules exclude.
/// </para>
/// </remarks>
internal sealed class GetAllStoriesHandler(
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor,
    IStoryAccessService storyAccessService,
    IStoryResponseBuilder storyResponseBuilder)
    : RpcResultObjectHandler<RequestGetAllStories, IAllStories>
{
    /// <summary>Peers returned per page; the client pages with the <c>next</c> flag and <c>state</c>.</summary>
    private const int PeerPageSize = 100;

    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");
    private readonly IMongoCollection<BsonDocument> _storyReadsCollection =
        mongoDatabase.GetCollection<BsonDocument>("story_reads");
    private readonly IMongoCollection<BsonDocument> _hiddenCollection =
        mongoDatabase.GetCollection<BsonDocument>("story_hidden_peers");

    protected override async Task<IAllStories> HandleCoreAsync(IRequestInput input, RequestGetAllStories obj)
    {
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var (hiddenPeerIds, allHidden) = await LoadHiddenAsync(input.UserId);

        var contactIds = await queryProcessor.ProcessAsync(new GetContactUserIdListQuery(input.UserId));
        var channelIds = await queryProcessor.ProcessAsync(new GetChannelIdListByUserIdQuery(input.UserId));

        // The caller's own stories always belong in their own list.
        var visibleUserIds = contactIds.Append(input.UserId).Distinct().ToList();

        var stories = await LoadCandidateStoriesAsync(visibleUserIds, channelIds.Distinct().ToList(), currentTime);

        var context = await storyAccessService.GetViewerContextAsync(
            input.UserId,
            stories.Where(s => s.OwnerPeerType == StoryHelper.PeerTypeUser).Select(s => s.OwnerPeerId));

        var visible = storyAccessService.FilterVisible(stories, input.UserId, context);

        var groups = visible
            .GroupBy(s => (OwnerPeerId: s.OwnerPeerId, OwnerPeerType: s.OwnerPeerType))
            .Where(g => IsInRequestedSection(g.Key.OwnerPeerId, hiddenPeerIds, allHidden, obj.Hidden))
            // Peers with the freshest story first, which is the order clients render the bar in.
            .OrderByDescending(g => g.Max(s => s.Date))
            .ToList();

        var totalPeerCount = groups.Count;
        var offset = ParseOffset(obj.State, obj.Next);
        var page = groups.Skip(offset).Take(PeerPageSize).ToList();

        var readsMap = await LoadReadsAsync(input.UserId);
        var pagedStories = page.SelectMany(g => g).ToList();
        var sentReactions = await storyResponseBuilder.GetSentReactionsAsync(pagedStories, input.UserId);

        var peerStoriesList = new TVector<MyTelegram.Schema.IPeerStories>();

        foreach (var group in page)
        {
            var ownerPeerId = group.Key.OwnerPeerId;
            var ownerPeerType = group.Key.OwnerPeerType;
            var isOwner = ownerPeerType == StoryHelper.PeerTypeUser && ownerPeerId == input.UserId;

            var storyItems = new TVector<IStoryItem>();
            foreach (var story in group.OrderBy(s => s.StoryId))
            {
                sentReactions.TryGetValue((ownerPeerId, ownerPeerType, story.StoryId), out var sentReaction);
                storyItems.Add(StoryHelper.ConvertToStoryItem(story, input.UserId, sentReaction, isOwner));
            }

            readsMap.TryGetValue((ownerPeerId, ownerPeerType), out var maxReadId);

            peerStoriesList.Add(new MyTelegram.Schema.TPeerStories
            {
                Peer = StoryHelper.CreatePeer(ownerPeerType, ownerPeerId),
                Stories = storyItems,
                MaxReadId = maxReadId > 0 ? maxReadId : null
            });
        }

        var peers = await storyResponseBuilder.BuildPeersAsync(input, pagedStories);

        var consumed = offset + page.Count;
        var hasMore = consumed < totalPeerCount;

        return new TAllStories
        {
            Chats = peers.Chats,
            Users = peers.Users,
            StealthMode = BuildStealthMode(context.StealthMode),
            HasMore = hasMore,
            Count = totalPeerCount,
            State = (hasMore ? consumed : 0).ToString(),
            PeerStories = peerStoriesList
        };
    }

    private async Task<List<StoryDocument>> LoadCandidateStoriesAsync(
        List<long> userIds,
        List<long> channelIds,
        long currentTime)
    {
        if (userIds.Count == 0 && channelIds.Count == 0)
        {
            return [];
        }

        var filterBuilder = Builders<StoryDocument>.Filter;
        var ownerFilters = new List<FilterDefinition<StoryDocument>>();

        if (userIds.Count > 0)
        {
            ownerFilters.Add(filterBuilder.And(
                filterBuilder.Eq(s => s.OwnerPeerType, StoryHelper.PeerTypeUser),
                filterBuilder.In(s => s.OwnerPeerId, userIds)));
        }

        if (channelIds.Count > 0)
        {
            ownerFilters.Add(filterBuilder.And(
                filterBuilder.Eq(s => s.OwnerPeerType, StoryHelper.PeerTypeChannel),
                filterBuilder.In(s => s.OwnerPeerId, channelIds)));
        }

        var filter = filterBuilder.And(
            filterBuilder.Eq(s => s.Deleted, false),
            filterBuilder.Lte(s => s.Date, currentTime),
            filterBuilder.Gte(s => s.ExpireDate, currentTime),
            filterBuilder.Or(ownerFilters));

        return await _storyCollection.Find(filter).SortByDescending(s => s.Date).ToListAsync();
    }

    /// <summary>
    /// Reads both the per-peer hidden flags and the global one written by stories.toggleAllStoriesHidden.
    /// </summary>
    private async Task<(HashSet<long> hiddenPeerIds, bool allHidden)> LoadHiddenAsync(long userId)
    {
        var docs = await _hiddenCollection
            .Find(Builders<BsonDocument>.Filter.Eq("userId", userId))
            .ToListAsync();

        var hiddenPeerIds = new HashSet<long>();
        var allHidden = false;

        foreach (var doc in docs)
        {
            if (!doc.Contains("peerId") || !doc.Contains("hidden"))
            {
                continue;
            }

            var peerId = doc["peerId"].AsInt64;
            var hidden = doc["hidden"].AsBoolean;

            if (peerId == ToggleAllStoriesHiddenHandler.AllPeersId)
            {
                allHidden = hidden;
            }
            else if (hidden)
            {
                hiddenPeerIds.Add(peerId);
            }
        }

        return (hiddenPeerIds, allHidden);
    }

    /// <summary>
    /// Decides whether a peer belongs to the requested section. The global "hide all" flag flips the
    /// default for peers with no explicit per-peer setting.
    /// </summary>
    private static bool IsInRequestedSection(
        long ownerPeerId,
        HashSet<long> hiddenPeerIds,
        bool allHidden,
        bool requestedHidden)
    {
        var isHidden = allHidden || hiddenPeerIds.Contains(ownerPeerId);
        return isHidden == requestedHidden;
    }

    private async Task<Dictionary<(long, int), int>> LoadReadsAsync(long userId)
    {
        var reads = await _storyReadsCollection
            .Find(Builders<BsonDocument>.Filter.Eq("userId", userId))
            .ToListAsync();

        var readsMap = new Dictionary<(long, int), int>();

        foreach (var doc in reads)
        {
            if (!doc.Contains("ownerPeerId") || !doc.Contains("ownerPeerType"))
            {
                continue;
            }

            readsMap[(doc["ownerPeerId"].AsInt64, doc["ownerPeerType"].AsInt32)] =
                doc.Contains("maxReadId") ? doc["maxReadId"].AsInt32 : 0;
        }

        return readsMap;
    }

    /// <summary>The pagination cursor is simply the peer offset to resume from.</summary>
    private static int ParseOffset(string? state, bool next)
    {
        return next && int.TryParse(state, out var parsed) && parsed > 0 ? parsed : 0;
    }

    private static IStoriesStealthMode BuildStealthMode(StoryStealthDocument? stealth)
    {
        return new TStoriesStealthMode
        {
            ActiveUntilDate = stealth?.ActiveUntilDate,
            CooldownUntilDate = stealth?.CooldownUntilDate
        };
    }
}
