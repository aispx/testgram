using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

/// <summary>
/// Globally search for <a href="https://corefork.telegram.org/api/stories">stories</a> using a hashtag or a <a href="https://corefork.telegram.org/api/stories#location-tags">location media area</a>, see <a href="https://corefork.telegram.org/api/stories#searching-stories">here »</a> for more info on the full flow.Either <code>hashtag</code> <strong>or</strong> <code>area</code> <strong>must</strong> be set when invoking the method.
/// Possible errors
/// Code Type Description
/// 400 HASHTAG_INVALID The specified hashtag is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stories.searchPosts"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// <para>
/// Only public stories are searchable: user stories need an explicit allow-all privacy rule, and channel
/// stories need the channel to have a username. Hashtags are indexed onto
/// <see cref="StoryDocument.Hashtags"/> when the story is posted or edited.
/// </para>
/// </remarks>
internal sealed class SearchPostsHandler(
    IMongoDatabase mongoDatabase,
    IChannelAppService channelAppService,
    IStoryResponseBuilder storyResponseBuilder,
    IFileReferenceHelper fileReferenceHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Stories.RequestSearchPosts, IFoundStories>
{
    private const int MaxLimit = 100;
    private const int DefaultLimit = 20;

    /// <summary>Half-degree box around the requested point, ~55 km — matches venue-tag granularity.</summary>
    private const double GeoSearchRadiusDegrees = 0.5;

    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");

    protected override async Task<IFoundStories> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Stories.RequestSearchPosts obj)
    {
        var hashtag = StoryHelper.NormalizeHashtag(obj.Hashtag);
        var area = StoryMediaAreaHelper.ParseOne(obj.Area);

        // Exactly one of hashtag/area must be set.
        if (hashtag.Length == 0 == (area == null))
        {
            RpcErrors.RpcErrors400.HashtagInvalid.ThrowRpcError();
        }

        if (area != null && (!area.GeoLat.HasValue || !area.GeoLong.HasValue))
        {
            // Only location areas are searchable; a venue with no resolved coordinates is not.
            RpcErrors.RpcErrors400.HashtagInvalid.ThrowRpcError();
        }

        var limit = obj.Limit > 0 ? Math.Min(obj.Limit, MaxLimit) : DefaultLimit;
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var filterBuilder = Builders<StoryDocument>.Filter;
        var filter = filterBuilder.And(
            filterBuilder.Eq(s => s.Deleted, false),
            filterBuilder.Lte(s => s.Date, currentTime),
            filterBuilder.Gte(s => s.ExpireDate, currentTime)
        );

        if (hashtag.Length > 0)
        {
            filter = filterBuilder.And(filter, filterBuilder.AnyEq(s => s.Hashtags, hashtag));
        }
        else
        {
            // Narrow by the stored bounding box first, then verify precisely in memory.
            filter = filterBuilder.And(
                filter,
                filterBuilder.ElemMatch(
                    s => s.MediaAreas,
                    Builders<StoryMediaArea>.Filter.And(
                        Builders<StoryMediaArea>.Filter.Gte(a => a.GeoLat, area!.GeoLat!.Value - GeoSearchRadiusDegrees),
                        Builders<StoryMediaArea>.Filter.Lte(a => a.GeoLat, area.GeoLat!.Value + GeoSearchRadiusDegrees),
                        Builders<StoryMediaArea>.Filter.Gte(a => a.GeoLong, area.GeoLong!.Value - GeoSearchRadiusDegrees),
                        Builders<StoryMediaArea>.Filter.Lte(a => a.GeoLong, area.GeoLong!.Value + GeoSearchRadiusDegrees))));
        }

        if (TryParseOffset(obj.Offset, out var offsetDate, out var offsetStoryId))
        {
            filter = filterBuilder.And(
                filter,
                filterBuilder.Or(
                    filterBuilder.Lt(s => s.Date, offsetDate),
                    filterBuilder.And(
                        filterBuilder.Eq(s => s.Date, offsetDate),
                        filterBuilder.Lt(s => s.StoryId, offsetStoryId))));
        }

        // Over-fetch, because the public/geo checks below can only be applied after loading.
        var candidates = await _storyCollection.Find(filter)
            .SortByDescending(s => s.Date)
            .ThenByDescending(s => s.StoryId)
            .Limit(limit * 4)
            .ToListAsync();

        if (area != null)
        {
            candidates = candidates
                .Where(s => s.MediaAreas.Any(a => StoryMediaAreaHelper.MatchesGeo(
                    a, area.GeoLat!.Value, area.GeoLong!.Value, GeoSearchRadiusDegrees)))
                .ToList();
        }

        var publicStories = new List<StoryDocument>();
        foreach (var story in candidates)
        {
            if (publicStories.Count == limit)
            {
                break;
            }

            if (await IsPubliclySearchableAsync(story))
            {
                publicStories.Add(story);
            }
        }

        var sentReactions = await storyResponseBuilder.GetSentReactionsAsync(publicStories, input.UserId);

        var foundStories = new TVector<IFoundStory>();
        foreach (var story in publicStories)
        {
            sentReactions.TryGetValue(
                (story.OwnerPeerId, story.OwnerPeerType, story.StoryId), out var sentReaction);

            foundStories.Add(new TFoundStory
            {
                Peer = StoryHelper.CreatePeer(story.OwnerPeerType, story.OwnerPeerId),
                Story = StoryHelper.ConvertToStoryItem(fileReferenceHelper, story, input.UserId, sentReaction)
            });
        }

        var peers = await storyResponseBuilder.BuildPeersAsync(input, publicStories);

        var last = publicStories.Count == limit ? publicStories[^1] : null;

        return new TFoundStories
        {
            Count = foundStories.Count,
            Stories = foundStories,
            NextOffset = last != null ? $"{last.Date}_{last.StoryId}" : null,
            Chats = peers.Chats,
            Users = peers.Users
        };
    }

    /// <summary>
    /// A story shows up in global search only if anyone could have seen it anyway: an explicitly
    /// unrestricted user story, or a story of a channel with a public username.
    /// </summary>
    private async Task<bool> IsPubliclySearchableAsync(StoryDocument story)
    {
        if (story.OwnerPeerType == StoryHelper.PeerTypeChannel)
        {
            var channel = await channelAppService.GetAsync((long?)story.OwnerPeerId);
            return !string.IsNullOrEmpty(channel?.UserName);
        }

        if (story.OwnerPeerType != StoryHelper.PeerTypeUser)
        {
            return false;
        }

        // An empty rule set is treated conservatively as non-public rather than exposed globally.
        return story.PrivacyRules.Any(r => r.Type == StoryPrivacyRuleType.AllowAll) &&
               story.PrivacyRules.All(r => r.Type != StoryPrivacyRuleType.DisallowAll);
    }

    private static bool TryParseOffset(string? offset, out long date, out int storyId)
    {
        date = 0;
        storyId = 0;

        if (string.IsNullOrEmpty(offset))
        {
            return false;
        }

        var parts = offset.Split('_');
        return parts.Length == 2 &&
               long.TryParse(parts[0], out date) &&
               int.TryParse(parts[1], out storyId);
    }
}
