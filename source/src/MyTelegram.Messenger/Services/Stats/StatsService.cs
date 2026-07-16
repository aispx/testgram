using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema.Stats;

namespace MyTelegram.Messenger.Services.Stats;

/// <summary>
/// The Stats_Service. Assembles the statistics result objects (broadcast, megagroup, message, story,
/// public forwards) and resolves async graphs from the storage components and the Graph_Builder.
/// </summary>
/// <remarks>
/// Task 8.1 implements <see cref="GetBroadcastStatsAsync"/> and <see cref="GetMegagroupStatsAsync"/>.
/// Task 8.2 implements <see cref="GetMessageStatsAsync"/> and <see cref="GetStoryStatsAsync"/>.
/// Task 8.3 implements <see cref="GetMessagePublicForwardsAsync"/> and <see cref="GetStoryPublicForwardsAsync"/>.
/// Task 8.4 implements <see cref="LoadAsyncGraphAsync"/>.
/// </remarks>
public class StatsService(
    IMetricsStore metricsStore,
    IGraphBuilder graphBuilder,
    IUserConverterService userConverterService,
    IChatConverterService chatConverterService,
    IPublicForwardStore publicForwardStore,
    IAsyncGraphStore asyncGraphStore,
    IMessageConverterService messageConverterService,
    IMessageAppService messageAppService,
    IQueryProcessor queryProcessor,
    IMongoDatabase mongoDatabase,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options)
    : IStatsService, ITransientDependency
{
    private const string StoriesCollectionName = "stories";

    private const long MillisPerSecond = 1000L;

    /// <summary>
    /// The reporting window in days used to compute the <c>period</c> (Requirement 10.3), surfaced through
    /// the server settings mechanism (<see cref="MyTelegramMessengerServerOptions.Stats"/>, default 7,
    /// valid 1..365). The Metrics_Store clamps the value to the valid range when computing the period.
    /// </summary>
    private int ReportingWindowDays => options.CurrentValue.Stats.ReportingWindowDays;

    public async Task<IBroadcastStats> GetBroadcastStatsAsync(IRequestInput input, long channelId, bool dark)
    {
        var channel = new StatsEntityKey(StatsEntityType.Channel, channelId, 0);
        var nowUnix = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var period = await metricsStore.GetPeriodAsync(channel, ReportingWindowDays);
        var snapshotId = BuildSnapshotId("broadcast", channelId, period);

        // statsAbsValueAndPrev fields: current = aggregate over the Period, previous = aggregate over the
        // Previous_Period; AggregateAsync returns 0 for a range with no recorded metric (Requirement 2.8).
        var followers = await AbsValueAsync(channel, StatsMetricNames.Followers, period);
        var viewsPerPost = await AbsValueAsync(channel, StatsMetricNames.Views, period);
        var sharesPerPost = await AbsValueAsync(channel, StatsMetricNames.Shares, period);
        var reactionsPerPost = await AbsValueAsync(channel, StatsMetricNames.Reactions, period);
        var viewsPerStory = await AbsValueAsync(channel, StatsMetricNames.Views, period);
        var sharesPerStory = await AbsValueAsync(channel, StatsMetricNames.Shares, period);
        var reactionsPerStory = await AbsValueAsync(channel, StatsMetricNames.Reactions, period);

        // enabled_notifications: part = notifications-enabled count, total = subscriber count (Requirement 2.5).
        var notifyOn = await metricsStore.AggregateAsync(channel, StatsMetricNames.NotifyOn, period.MinDate, period.MaxDate);
        var muted = await metricsStore.AggregateAsync(channel, StatsMetricNames.Muted, period.MinDate, period.MaxDate);
        var subscriberCount = notifyOn + muted;
        var enabledNotifications = new TStatsPercentValue { Part = notifyOn, Total = subscriberCount };

        var recentPosts = await metricsStore.GetRecentPostInteractionsAsync(channelId);

        return new TBroadcastStats
        {
            Period = ToDateRange(period),
            Followers = followers,
            ViewsPerPost = viewsPerPost,
            SharesPerPost = sharesPerPost,
            ReactionsPerPost = reactionsPerPost,
            ViewsPerStory = viewsPerStory,
            SharesPerStory = sharesPerStory,
            ReactionsPerStory = reactionsPerStory,
            EnabledNotifications = enabledNotifications,
            GrowthGraph = await BuildSeriesGraphAsync(channel, StatsMetricNames.Followers, period, "Growth", "primary", GraphKind.Line, dark, snapshotId, nowUnix),
            FollowersGraph = await BuildSeriesGraphAsync(channel, StatsMetricNames.Followers, period, "Followers", "primary", GraphKind.Line, dark, snapshotId, nowUnix),
            MuteGraph = await BuildSeriesGraphAsync(channel, StatsMetricNames.Muted, period, "Muted", "secondary", GraphKind.Line, dark, snapshotId, nowUnix),
            TopHoursGraph = await BuildSeriesGraphAsync(channel, StatsMetricNames.Views, period, "Views by hour", "tertiary", GraphKind.Bar, dark, snapshotId, nowUnix),
            InteractionsGraph = await BuildSeriesGraphAsync(channel, StatsMetricNames.Views, period, "Interactions", "primary", GraphKind.Line, dark, snapshotId, nowUnix),
            IvInteractionsGraph = await BuildSeriesGraphAsync(channel, StatsMetricNames.Views, period, "IV interactions", "secondary", GraphKind.Line, dark, snapshotId, nowUnix),
            ViewsBySourceGraph = await BuildCategoryGraphAsync(channel, StatsMetricNames.Views, period, GraphKind.StackedBar, dark, snapshotId, nowUnix),
            NewFollowersBySourceGraph = await BuildCategoryGraphAsync(channel, StatsMetricNames.Followers, period, GraphKind.StackedBar, dark, snapshotId, nowUnix),
            LanguagesGraph = await BuildCategoryGraphAsync(channel, StatsMetricNames.Followers, period, GraphKind.Pie, dark, snapshotId, nowUnix),
            ReactionsByEmotionGraph = await BuildCategoryGraphAsync(channel, StatsMetricNames.Reactions, period, GraphKind.StackedBar, dark, snapshotId, nowUnix),
            StoryInteractionsGraph = await BuildSeriesGraphAsync(channel, StatsMetricNames.Shares, period, "Story interactions", "tertiary", GraphKind.Line, dark, snapshotId, nowUnix),
            StoryReactionsByEmotionGraph = await BuildCategoryGraphAsync(channel, StatsMetricNames.Reactions, period, GraphKind.StackedBar, dark, snapshotId, nowUnix),
            RecentPostsInteractions = new TVector<IPostInteractionCounters>(recentPosts.Select(ToPostInteractionCounters))
        };
    }

    public async Task<IMegagroupStats> GetMegagroupStatsAsync(IRequestInput input, long channelId, bool dark)
    {
        var channel = new StatsEntityKey(StatsEntityType.Channel, channelId, 0);
        var nowUnix = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var period = await metricsStore.GetPeriodAsync(channel, ReportingWindowDays);
        var snapshotId = BuildSnapshotId("megagroup", channelId, period);

        var members = await AbsValueAsync(channel, StatsMetricNames.Members, period);
        var messages = await AbsValueAsync(channel, StatsMetricNames.Messages, period);
        var viewers = await AbsValueAsync(channel, StatsMetricNames.Viewers, period);
        var posters = await AbsValueAsync(channel, StatsMetricNames.Posters, period);

        var topEntities = await metricsStore.GetTopEntitiesAsync(channelId, period.MinDate, period.MaxDate);
        var users = await BuildUsersAsync(input, topEntities.UserIds);

        return new TMegagroupStats
        {
            Period = ToDateRange(period),
            Members = members,
            Messages = messages,
            Viewers = viewers,
            Posters = posters,
            GrowthGraph = await BuildSeriesGraphAsync(channel, StatsMetricNames.Members, period, "Growth", "primary", GraphKind.Line, dark, snapshotId, nowUnix),
            MembersGraph = await BuildSeriesGraphAsync(channel, StatsMetricNames.Members, period, "Members", "primary", GraphKind.Line, dark, snapshotId, nowUnix),
            NewMembersBySourceGraph = await BuildCategoryGraphAsync(channel, StatsMetricNames.Members, period, GraphKind.StackedBar, dark, snapshotId, nowUnix),
            LanguagesGraph = await BuildCategoryGraphAsync(channel, StatsMetricNames.Members, period, GraphKind.Pie, dark, snapshotId, nowUnix),
            MessagesGraph = await BuildSeriesGraphAsync(channel, StatsMetricNames.Messages, period, "Messages", "secondary", GraphKind.StackedBar, dark, snapshotId, nowUnix),
            ActionsGraph = await BuildSeriesGraphAsync(channel, StatsMetricNames.Messages, period, "Actions", "tertiary", GraphKind.Line, dark, snapshotId, nowUnix),
            TopHoursGraph = await BuildSeriesGraphAsync(channel, StatsMetricNames.Messages, period, "Activity by hour", "primary", GraphKind.Bar, dark, snapshotId, nowUnix),
            WeekdaysGraph = await BuildCategoryGraphAsync(channel, StatsMetricNames.Messages, period, GraphKind.StackedBar, dark, snapshotId, nowUnix),
            TopPosters = new TVector<IStatsGroupTopPoster>(topEntities.Posters.Select(p =>
                (IStatsGroupTopPoster)new TStatsGroupTopPoster { UserId = p.UserId, Messages = p.Messages, AvgChars = p.AvgChars })),
            TopAdmins = new TVector<IStatsGroupTopAdmin>(topEntities.Admins.Select(a =>
                (IStatsGroupTopAdmin)new TStatsGroupTopAdmin { UserId = a.UserId, Deleted = a.Deleted, Kicked = a.Kicked, Banned = a.Banned })),
            TopInviters = new TVector<IStatsGroupTopInviter>(topEntities.Inviters.Select(i =>
                (IStatsGroupTopInviter)new TStatsGroupTopInviter { UserId = i.UserId, Invitations = i.Invitations })),
            Users = users
        };
    }

    // --- Task 8.2: per-item (message/story) statistics assembly ---

    /// <summary>
    /// Assembles <c>stats.messageStats</c> for a channel post. The <c>views_graph</c> is the per-day view
    /// series and the <c>reactions_by_emotion_graph</c> is the per-emotion reaction breakdown, both over the
    /// Period (Requirements 4.1, 4.3). An item with no recorded metric yields an empty <c>statsGraph</c>
    /// rather than a <c>statsGraphError</c> (Requirement 4.4, handled by the Graph_Builder). When the
    /// <paramref name="msgId"/> does not identify an existing post in the resolved channel, the service
    /// raises <c>MESSAGE_ID_INVALID</c> (Requirement 4.2).
    /// </summary>
    public async Task<IMessageStats> GetMessageStatsAsync(IRequestInput input, long channelId, int msgId, bool dark)
    {
        // Existence check (Requirement 4.2): the msg_id must identify an existing post in the channel.
        // A channel post is stored with OwnerPeerId == channelId; the read model is the message existence
        // source of truth used across the messages handlers.
        var message = await queryProcessor.ProcessAsync(new GetMessageByPeerIdAndMessageIdQuery(channelId, msgId));
        if (message == null)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        var entity = new StatsEntityKey(StatsEntityType.Message, channelId, msgId);
        var nowUnix = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var period = await metricsStore.GetPeriodAsync(entity, ReportingWindowDays);
        var snapshotId = BuildItemSnapshotId("message", channelId, msgId, period);

        return new TMessageStats
        {
            ViewsGraph = await BuildSeriesGraphAsync(entity, StatsMetricNames.Views, period, "Views", "primary", GraphKind.Line, dark, snapshotId, nowUnix),
            ReactionsByEmotionGraph = await BuildCategoryGraphAsync(entity, StatsMetricNames.Reactions, period, GraphKind.StackedBar, dark, snapshotId, nowUnix)
        };
    }

    /// <summary>
    /// Assembles <c>stats.storyStats</c> for a story, with <c>views_graph</c> and
    /// <c>reactions_by_emotion_graph</c> over the Period (Requirements 5.1, 5.4). When the resolved peer has
    /// never posted a story the service raises <c>STORIES_NEVER_CREATED</c> (Requirement 5.2); when the
    /// peer has posted stories but the supplied id does not identify one, it raises <c>PEER_ID_INVALID</c>
    /// (Requirement 5.3). The peer itself is resolved and authorized by the Access_Controller.
    /// </summary>
    public async Task<IStoryStats> GetStoryStatsAsync(IRequestInput input, Peer peer, int storyId, bool dark)
    {
        await EnsureStoryExistsAsync(peer, storyId);

        var entity = new StatsEntityKey(StatsEntityType.Story, peer.PeerId, storyId);
        var nowUnix = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var period = await metricsStore.GetPeriodAsync(entity, ReportingWindowDays);
        var snapshotId = BuildItemSnapshotId("story", peer.PeerId, storyId, period);

        return new TStoryStats
        {
            ViewsGraph = await BuildSeriesGraphAsync(entity, StatsMetricNames.Views, period, "Views", "primary", GraphKind.Line, dark, snapshotId, nowUnix),
            ReactionsByEmotionGraph = await BuildCategoryGraphAsync(entity, StatsMetricNames.Reactions, period, GraphKind.StackedBar, dark, snapshotId, nowUnix)
        };
    }

    // --- Task 8.3: public-forwards assembly ---

    /// <summary>
    /// Assembles <c>stats.publicForwards</c> for a channel message (Requirements 6.1, 6.2, 6.4). When the
    /// <paramref name="msgId"/> does not identify an existing message the service raises
    /// <c>MESSAGE_ID_INVALID</c> (Requirement 6.5). An unrecognized non-empty <paramref name="offset"/>
    /// surfaces as an <see cref="InvalidStatsOffsetException"/> thrown by the store, which the handler maps
    /// to an invalid-offset error (Requirement 6.8).
    /// </summary>
    public async Task<IPublicForwards> GetMessagePublicForwardsAsync(IRequestInput input, long channelId, int msgId, string offset, int limit)
    {
        // Existence check (Requirement 6.5).
        var message = await queryProcessor.ProcessAsync(new GetMessageByPeerIdAndMessageIdQuery(channelId, msgId));
        if (message == null)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        var source = new ForwardSourceKey(ForwardSourceType.Message, channelId, msgId);
        return await BuildPublicForwardsAsync(input, source, offset, limit);
    }

    /// <summary>
    /// Assembles <c>stats.publicForwards</c> for a story (Requirements 7.1, 7.2, 7.4). Story existence is
    /// verified first (<c>STORIES_NEVER_CREATED</c> / <c>PEER_ID_INVALID</c>, Requirements 7.6, 7.7). An
    /// unrecognized non-empty <paramref name="offset"/> surfaces as an <see cref="InvalidStatsOffsetException"/>.
    /// </summary>
    public async Task<IPublicForwards> GetStoryPublicForwardsAsync(IRequestInput input, Peer peer, int storyId, string offset, int limit)
    {
        await EnsureStoryExistsAsync(peer, storyId);

        var source = new ForwardSourceKey(ForwardSourceType.Story, peer.PeerId, storyId);
        return await BuildPublicForwardsAsync(input, source, offset, limit);
    }

    // --- Task 8.4: async graph resolution + error mapping ---

    /// <summary>
    /// Resolves an Async_Graph_Token (with optional zoom <paramref name="x"/>) via the Async_Graph_Store and
    /// serializes the resolved spec through the Graph_Builder (Requirements 9.2, 9.3). Resolution outcomes
    /// map to RPC errors in the fixed precedence enforced by the store: <c>Invalid</c>/<c>ZoomInvalid</c> →
    /// <c>GRAPH_INVALID_RELOAD</c> (Requirements 9.4, 9.8), <c>Expired</c> → <c>GRAPH_EXPIRED_RELOAD</c>
    /// (Requirement 9.5), <c>Outdated</c> → <c>GRAPH_OUTDATED_RELOAD</c> (Requirement 9.6). A post-resolution
    /// serialization failure surfaces as a <c>statsGraphError</c> returned by the Graph_Builder (Requirement 9.7).
    /// </summary>
    public async Task<IStatsGraph> LoadAsyncGraphAsync(IRequestInput input, string token, long? x)
    {
        var nowUnix = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var resolution = await asyncGraphStore.ResolveAsync(token, x, nowUnix);

        switch (resolution.Status)
        {
            case AsyncGraphStatus.Ok:
                // On success serialize the resolved spec; the Graph_Builder yields a statsGraphError on a
                // post-resolution serialization failure (Requirement 9.7), which surfaces to the caller.
                return await graphBuilder.BuildInlineAsync(resolution.Spec!, resolution.Dark, token, nowUnix);

            case AsyncGraphStatus.Expired:
                RpcErrors.RpcErrors400.GraphExpiredReload.ThrowRpcError();
                break;

            case AsyncGraphStatus.Outdated:
                RpcErrors.RpcErrors400.GraphOutdatedReload.ThrowRpcError();
                break;

            case AsyncGraphStatus.Invalid:
            case AsyncGraphStatus.ZoomInvalid:
            default:
                RpcErrors.RpcErrors400.GraphInvalidReload.ThrowRpcError();
                break;
        }

        // Unreachable: every non-Ok branch throws an RPC error above.
        return null!;
    }

    // --- Helpers ---

    /// <summary>
    /// Builds a <c>statsAbsValueAndPrev</c> whose <c>current</c> is the aggregate over the Period and
    /// <c>previous</c> is the aggregate over the Previous_Period
    /// (<c>(min_date - (max_date - min_date))</c> .. <c>min_date</c>). Both are <c>0</c> when no metric is
    /// recorded in the corresponding range (Requirements 2.8, 3.7, 10.2).
    /// </summary>
    private async Task<IStatsAbsValueAndPrev> AbsValueAsync(StatsEntityKey entity, string metric, StatsDateRange period)
    {
        var current = await metricsStore.AggregateAsync(entity, metric, period.MinDate, period.MaxDate);

        var previousMin = period.MinDate - (period.MaxDate - period.MinDate);
        var previous = await metricsStore.AggregateAsync(entity, metric, previousMin, period.MinDate);

        return new TStatsAbsValueAndPrev { Current = current, Previous = previous };
    }

    /// <summary>
    /// Builds a single-series <c>statsGraph</c> from the per-day series of <paramref name="metric"/> over
    /// the Period. An empty series yields an empty <c>statsGraph</c> rather than a <c>statsGraphError</c>.
    /// </summary>
    private async Task<IStatsGraph> BuildSeriesGraphAsync(StatsEntityKey entity, string metric, StatsDateRange period,
        string seriesName, string colorKey, GraphKind kind, bool dark, string snapshotId, int nowUnix)
    {
        var points = await metricsStore.GetSeriesAsync(entity, metric, period.MinDate, period.MaxDate);

        var xAxis = points.Select(p => p.UtcDay * MillisPerSecond).ToList();
        var values = points.Select(p => p.Value).ToList();
        var series = new[] { new GraphSeries(metric, seriesName, colorKey, values) };

        var spec = new GraphSpec(kind, xAxis, series);
        return await graphBuilder.BuildInlineAsync(spec, dark, snapshotId, nowUnix);
    }

    /// <summary>
    /// Builds a multi-series <c>statsGraph</c> from the per-category per-day series of
    /// <paramref name="metric"/> over the Period. Categories are aligned onto a single, strictly-ascending
    /// x-axis (missing days contribute <c>0</c>). An absent breakdown yields an empty <c>statsGraph</c>.
    /// </summary>
    private async Task<IStatsGraph> BuildCategoryGraphAsync(StatsEntityKey entity, string metric, StatsDateRange period,
        GraphKind kind, bool dark, string snapshotId, int nowUnix)
    {
        var categorySeries = await metricsStore.GetCategorySeriesAsync(entity, metric, period.MinDate, period.MaxDate);

        // Unify the x-axis across all categories: the sorted set of distinct recorded days.
        var days = categorySeries
            .SelectMany(c => c.Points.Select(p => p.UtcDay))
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        var dayIndex = new Dictionary<int, int>(days.Count);
        for (var i = 0; i < days.Count; i++)
        {
            dayIndex[days[i]] = i;
        }

        var xAxis = days.Select(d => d * MillisPerSecond).ToList();

        var colorKeys = new[] { "primary", "secondary", "tertiary", "quaternary", "quinary" };
        var series = new List<GraphSeries>(categorySeries.Count);
        for (var c = 0; c < categorySeries.Count; c++)
        {
            var category = categorySeries[c];
            var values = new long[days.Count];
            foreach (var point in category.Points)
            {
                values[dayIndex[point.UtcDay]] += point.Value;
            }

            var colorKey = colorKeys[c % colorKeys.Length];
            series.Add(new GraphSeries(category.Category, category.Category, colorKey, values));
        }

        var spec = new GraphSpec(kind, xAxis, series);
        return await graphBuilder.BuildInlineAsync(spec, dark, snapshotId, nowUnix);
    }

    /// <summary>
    /// Resolves the distinct top-entity user ids into the <c>users</c> vector (Requirements 3.3, 3.5).
    /// </summary>
    private async Task<TVector<IUser>> BuildUsersAsync(IRequestInput input, IReadOnlyList<long> userIds)
    {
        if (userIds.Count == 0)
        {
            return new TVector<IUser>();
        }

        var userList = await userConverterService.GetUserListAsync(input, userIds.ToList(), false, false, input.Layer);
        return new TVector<IUser>(userList.Cast<IUser>());
    }

    private static IPostInteractionCounters ToPostInteractionCounters(PostInteraction interaction) =>
        interaction.Type == StatsEntityType.Story
            ? new TPostInteractionCountersStory
            {
                StoryId = interaction.ItemId,
                Views = interaction.Views,
                Forwards = interaction.Forwards,
                Reactions = interaction.Reactions
            }
            : new TPostInteractionCountersMessage
            {
                MsgId = interaction.ItemId,
                Views = interaction.Views,
                Forwards = interaction.Forwards,
                Reactions = interaction.Reactions
            };

    private static IStatsDateRangeDays ToDateRange(StatsDateRange period) =>
        new TStatsDateRangeDays { MinDate = period.MinDate, MaxDate = period.MaxDate };

    private static string BuildSnapshotId(string prefix, long channelId, StatsDateRange period) =>
        $"{prefix}:{channelId}:{period.MaxDate}";

    private static string BuildItemSnapshotId(string prefix, long ownerPeerId, int itemId, StatsDateRange period) =>
        $"{prefix}:{ownerPeerId}:{itemId}:{period.MaxDate}";

    /// <summary>
    /// Verifies that the resolved <paramref name="peer"/> has posted at least one story
    /// (<c>STORIES_NEVER_CREATED</c> otherwise) and that <paramref name="storyId"/> identifies one of its
    /// stories (<c>PEER_ID_INVALID</c> otherwise), per Requirements 5.2/5.3 and 7.6/7.7. Stories are held in
    /// the document store rather than as event-sourced read models, so existence is checked against the
    /// <c>stories</c> collection directly.
    /// </summary>
    private async Task EnsureStoryExistsAsync(Peer peer, int storyId)
    {
        var ownerPeerType = StoryHelper.ToStoryPeerType(peer.PeerType);
        var stories = mongoDatabase.GetCollection<StoryDocument>(StoriesCollectionName);

        var peerFilter = Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, peer.PeerId),
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, ownerPeerType),
            Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false));

        var hasAnyStory = await stories.Find(peerFilter).AnyAsync();
        if (!hasAnyStory)
        {
            RpcErrors.RpcErrors400.StoriesNeverCreated.ThrowRpcError();
        }

        var storyFilter = Builders<StoryDocument>.Filter.And(
            peerFilter,
            Builders<StoryDocument>.Filter.Eq(s => s.StoryId, storyId));

        var storyExists = await stories.Find(storyFilter).AnyAsync();
        if (!storyExists)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }
    }

    /// <summary>
    /// Reads a page of public forwards for <paramref name="source"/> from the Public_Forward_Store and maps
    /// it to a <c>stats.publicForwards</c> object: each recorded forward becomes a <c>publicForwardMessage</c>
    /// resolved from its forwarding channel message, <c>count</c> is the store's total, <c>next_offset</c> is
    /// carried through (set only when more forwards remain), and the referenced <c>chats</c>/<c>users</c> are
    /// resolved via the converters (Requirements 6.1, 6.2, 6.4, 7.1, 7.2, 7.4). An unrecognized non-empty
    /// offset propagates as an <see cref="InvalidStatsOffsetException"/> thrown by the store.
    /// </summary>
    private async Task<IPublicForwards> BuildPublicForwardsAsync(IRequestInput input, ForwardSourceKey source, string offset, int limit)
    {
        var page = await publicForwardStore.GetPageAsync(source, offset, limit);

        var forwards = new TVector<IPublicForward>();
        var readModels = new List<IMessageReadModel>(page.Items.Count);

        foreach (var item in page.Items)
        {
            // A recorded forward references the forwarding channel message by (peerId, msgId). Resolve it to
            // a schema message; skip forwards whose message can no longer be read.
            var readModel = await queryProcessor.ProcessAsync(
                new GetMessageByPeerIdAndMessageIdQuery(item.ForwardingPeerId, item.ForwardingMsgId));
            if (readModel == null)
            {
                continue;
            }

            readModels.Add(readModel);
            var message = messageConverterService.ToMessage(input.UserId, readModel, layer: input.Layer);
            forwards.Add(new TPublicForwardMessage { Message = message });
        }

        // Collect the chat/user entities referenced by the resolved forward messages, plus the forwarding
        // channels themselves, so the response carries every referenced entity (Requirements 6.4, 7.4).
        var (userIds, channelIds) = messageAppService.GetExtraPeerIds(readModels);
        foreach (var item in page.Items)
        {
            channelIds.Add(item.ForwardingPeerId);
        }

        var chats = channelIds.Count == 0
            ? new List<IChat>()
            : await chatConverterService.GetChannelListAsync(input, channelIds.ToList(), layer: input.Layer);

        var users = userIds.Count == 0
            ? new List<ILayeredUser>()
            : await userConverterService.GetUserListAsync(input, userIds.ToList(), layer: input.Layer);

        return new TPublicForwards
        {
            Count = page.Count,
            Forwards = forwards,
            NextOffset = page.NextOffset,
            Chats = new TVector<IChat>(chats),
            Users = new TVector<IUser>(users.Cast<IUser>())
        };
    }
}
