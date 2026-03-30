using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Converters;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

internal sealed class GetAllStoriesHandler(
    IMongoDatabase mongoDatabase,
    IUserConverterService userConverterService)
    : RpcResultObjectHandler<RequestGetAllStories, IAllStories>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");
    private readonly IMongoCollection<BsonDocument> _storyReadsCollection =
        mongoDatabase.GetCollection<BsonDocument>("story_reads");
    private readonly IMongoCollection<BsonDocument> _hiddenCollection =
        mongoDatabase.GetCollection<BsonDocument>("story_hidden_peers");

    protected override async Task<IAllStories> HandleCoreAsync(IRequestInput input, RequestGetAllStories obj)
    {
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var hiddenFilter = Builders<BsonDocument>.Filter.Eq("userId", input.UserId);
        var hiddenDocs = await _hiddenCollection.Find(hiddenFilter).ToListAsync();
        var hiddenPeerIds = hiddenDocs
            .Where(d => d.Contains("peerId") && d.Contains("hidden") && d["hidden"].AsBoolean)
            .Select(d => d["peerId"].AsInt64)
            .ToHashSet();

        var filterBuilder = Builders<StoryDocument>.Filter;
        var filter = filterBuilder.And(
            filterBuilder.Eq(s => s.Deleted, false),
            filterBuilder.Lte(s => s.Date, currentTime),
            filterBuilder.Gte(s => s.ExpireDate, currentTime)
        );

        if (obj.Hidden)
        {
            filter = filterBuilder.And(filter, filterBuilder.Eq(s => s.Archived, true));
        }
        else
        {
            filter = filterBuilder.And(filter, filterBuilder.Eq(s => s.Archived, false));
        }

        var stories = await _storyCollection.Find(filter)
            .SortByDescending(s => s.Date)
            .ToListAsync();

        var allReadsFilter = Builders<BsonDocument>.Filter.Eq("userId", input.UserId);
        var allReads = await _storyReadsCollection.Find(allReadsFilter).ToListAsync();
        var readsMap = new Dictionary<(long, int), int>();
        foreach (var d in allReads)
        {
            if (d.Contains("ownerPeerId") && d.Contains("ownerPeerType"))
            {
                var key = (d["ownerPeerId"].AsInt64, d["ownerPeerType"].AsInt32);
                var maxReadId = d.Contains("maxReadId") ? d["maxReadId"].AsInt32 : 0;
                readsMap[key] = maxReadId;
            }
        }

        var count = stories.Count;

        var groupedStories = stories
            .GroupBy(s => new { s.OwnerPeerId, s.OwnerPeerType })
            .ToList();

        if (obj.Hidden)
        {
            groupedStories = groupedStories.Where(g => hiddenPeerIds.Contains(g.Key.OwnerPeerId)).ToList();
        }
        else
        {
            groupedStories = groupedStories.Where(g => !hiddenPeerIds.Contains(g.Key.OwnerPeerId)).ToList();
        }

        var peerStoriesList = new TVector<MyTelegram.Schema.IPeerStories>();
        var userIds = new List<long>();

        foreach (var group in groupedStories)
        {
            var ownerPeerId = group.Key.OwnerPeerId;
            var ownerPeerType = group.Key.OwnerPeerType;

            if (ownerPeerType == 0)
                userIds.Add(ownerPeerId);

            var storyItems = new TVector<IStoryItem>();

            readsMap.TryGetValue((ownerPeerId, ownerPeerType), out var maxReadId);

            foreach (var story in group.OrderByDescending(s => s.StoryId))
            {
                var storyItem = StoryHelper.ConvertToStoryItem(story, input.UserId);
                storyItems.Add(storyItem);
            }

            var peer = new MyTelegram.Schema.TPeerStories
            {
                Peer = StoryHelper.CreatePeer(ownerPeerType, ownerPeerId),
                Stories = storyItems,
                MaxReadId = maxReadId > 0 ? maxReadId : null
            };

            peerStoriesList.Add(peer);
        }

        var users = new TVector<IUser>();

        if (userIds.Count > 0)
        {
            var userList = await userConverterService.GetUserListAsync(input, userIds.Distinct().ToList(), false, false, input.Layer);
            foreach (var user in userList)
            {
                users.Add((IUser)user);
            }
        }

        return new TAllStories
        {
            Chats = new TVector<IChat>(),
            Users = users,
            StealthMode = new TStoriesStealthMode(),
            HasMore = false,
            Count = count,
            State = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            PeerStories = peerStoriesList
        };
    }
}
