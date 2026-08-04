using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Domain.Aggregates.UserConfig;
using MyTelegram.Messenger.Services.Stats;
using MyTelegram.Messenger.Services.Stats.Ingestion;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

/// <summary>
/// React to a story.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 REACTION_INVALID The specified reaction is invalid.
/// 400 STORY_ID_INVALID The specified story ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stories.sendReaction"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SendReactionHandler(
    IMongoDatabase mongoDatabase,
    IMetricsStore metricsStore,
    ICommandBus commandBus,
    IQueryProcessor queryProcessor,
    IStoryAccessService storyAccessService,
    IStoryUpdatesSender storyUpdatesSender)
    : RpcResultObjectHandler<MyTelegram.Schema.Stories.RequestSendReaction, IUpdates>
{
    private const string RecentKey = "recent_reactions";
    private const int MaxRecentReactions = 20;

    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");
    private readonly IMongoCollection<BsonDocument> _reactionsCollection =
        mongoDatabase.GetCollection<BsonDocument>("story_reactions");

    protected override async Task<IUpdates> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Stories.RequestSendReaction obj)
    {
        var (peerId, peerType) = await storyAccessService.ResolveReadablePeerAsync(obj.Peer, input.UserId);

        var storyFilter = Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, peerId),
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, peerType),
            Builders<StoryDocument>.Filter.Eq(s => s.StoryId, obj.StoryId),
            Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false)
        );

        var story = await _storyCollection.Find(storyFilter).FirstOrDefaultAsync();
        if (story == null)
        {
            RpcErrors.RpcErrors400.StoryIdInvalid.ThrowRpcError();
        }

        // Reacting requires being able to see the story in the first place.
        var context = await storyAccessService.GetViewerContextAsync(input.UserId, [peerId]);
        if (!StoryHelper.CanViewStory(story!, input.UserId, context))
        {
            RpcErrors.RpcErrors400.StoryIdInvalid.ThrowRpcError();
        }

        var reactionFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("storyOwnerPeerId", peerId),
            Builders<BsonDocument>.Filter.Eq("storyOwnerPeerType", peerType),
            Builders<BsonDocument>.Filter.Eq("storyId", obj.StoryId),
            Builders<BsonDocument>.Filter.Eq("userId", input.UserId)
        );

        var existingReaction = await _reactionsCollection.Find(reactionFilter).FirstOrDefaultAsync();
        var hadReaction = existingReaction != null &&
                          existingReaction.Contains("reaction") &&
                          !existingReaction["reaction"].IsBsonNull;

        IReaction sentReaction;

        if (obj.Reaction is TReactionEmpty or null)
        {
            if (existingReaction != null)
            {
                await _reactionsCollection.DeleteOneAsync(reactionFilter);

                if (hadReaction && story!.ReactionsCount > 0)
                {
                    await _storyCollection.UpdateOneAsync(
                        storyFilter,
                        Builders<StoryDocument>.Update.Inc(s => s.ReactionsCount, -1));
                }
            }

            sentReaction = new TReactionEmpty();
        }
        else
        {
            sentReaction = await SaveReactionAsync(
                obj, peerId, peerType, input.UserId, reactionFilter, storyFilter, hadReaction);

            if (obj.AddToRecent)
            {
                await SaveRecentReactionAsync(input, obj.Reaction);
            }
        }

        var updatedStory = await _storyCollection.Find(storyFilter).FirstOrDefaultAsync() ?? story!;
        var peer = StoryHelper.CreatePeer(peerType, peerId);

        // The poster's view of the story now has a different reaction count.
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
                new TUpdateSentStoryReaction
                {
                    Peer = peer,
                    StoryId = obj.StoryId,
                    Reaction = sentReaction
                }
            },
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Date = CurrentDate,
            Seq = 0
        };
    }

    private async Task<IReaction> SaveReactionAsync(
        MyTelegram.Schema.Stories.RequestSendReaction obj,
        long peerId,
        int peerType,
        long userId,
        FilterDefinition<BsonDocument> reactionFilter,
        FilterDefinition<StoryDocument> storyFilter,
        bool hadReaction)
    {
        var doc = new BsonDocument
        {
            { "storyOwnerPeerId", peerId },
            { "storyOwnerPeerType", peerType },
            { "storyId", obj.StoryId },
            { "userId", userId },
            { "date", DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
        };

        switch (obj.Reaction)
        {
            case TReactionEmoji emoji:
                doc["reaction"] = emoji.Emoticon;
                doc["type"] = "emoji";
                break;
            case TReactionCustomEmoji customEmoji:
                doc["reaction"] = customEmoji.DocumentId.ToString();
                doc["type"] = "custom";
                break;
            default:
                RpcErrors.RpcErrors400.ReactionInvalid.ThrowRpcError();
                break;
        }

        await _reactionsCollection.ReplaceOneAsync(reactionFilter, doc, new ReplaceOptions { IsUpsert = true });

        // Changing an existing reaction does not add a new reacting user.
        if (!hadReaction)
        {
            await _storyCollection.UpdateOneAsync(
                storyFilter,
                Builders<StoryDocument>.Update.Inc(s => s.ReactionsCount, 1));

            await RecordStatsAsync(obj, peerId, peerType);
        }

        return obj.Reaction;
    }

    private async Task RecordStatsAsync(
        MyTelegram.Schema.Stories.RequestSendReaction obj,
        long peerId,
        int peerType)
    {
        // Stats ingestion: per-story reactions with an emotion breakdown (reactions_by_emotion
        // graph) and, for channel-owned stories, the channel-level story-reactions counter.
        // Removals are not decremented (activity counter semantics, same as message reactions).
        var emotion = obj.Reaction switch
        {
            TReactionEmoji e => e.Emoticon,
            TReactionCustomEmoji c => $"custom:{c.DocumentId}",
            _ => "unknown"
        };

        var utcDay = StatsIngestionTime.CurrentUtcDay();

        await metricsStore.RecordAsync(
            new StatsEntityKey(StatsEntityType.Story, peerId, obj.StoryId), StatsMetricNames.Reactions,
            utcDay, 1, new Dictionary<string, long> { [emotion] = 1 });

        if (peerType == StoryHelper.PeerTypeChannel)
        {
            await metricsStore.RecordAsync(
                new StatsEntityKey(StatsEntityType.Channel, peerId, 0), StatsMetricNames.StoryReactions,
                utcDay, 1, new Dictionary<string, long> { [emotion] = 1 });
        }
    }

    /// <summary>
    /// Adds the emoji to the account's recent-reactions list, matching messages.sendReaction so both
    /// surfaces share one list.
    /// </summary>
    private async Task SaveRecentReactionAsync(IRequestInput input, IReaction reaction)
    {
        if (reaction is not TReactionEmoji emoji)
        {
            return;
        }

        var recentConfig = await queryProcessor.ProcessAsync(new GetUserConfigByKeyQuery(input.UserId, RecentKey));
        var existing = recentConfig?.Value?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList() ?? [];

        existing.Remove(emoji.Emoticon);
        existing.Insert(0, emoji.Emoticon);

        if (existing.Count > MaxRecentReactions)
        {
            existing = existing.Take(MaxRecentReactions).ToList();
        }

        var configId = UserConfigId.Create(input.UserId, RecentKey);
        await commandBus.PublishAsync(new UpdateUserConfigCommand(
            configId, input.ToRequestInfo(), input.UserId, RecentKey, string.Join(',', existing)));
    }
}
