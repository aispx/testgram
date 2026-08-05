using MongoDB.Bson;
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

    /// <summary>Per-user notify-settings read model, used to recompute the muted gauge on demand.</summary>
    private const string NotifySettingsCollectionName = "eventflow-peernotifysettingsreadmodel";

    /// <summary>Reading-history read model, used to count distinct supergroup viewers on demand.</summary>
    private const string ReadingHistoryCollectionName = "eventflow-readinghistoryreadmodel";

    private const long MillisPerSecond = 1000L;

    private const long MillisPerHour = 3_600_000L;

    private const int SecondsPerDay = 86_400;

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
        var graphPeriod = EffectiveGraphPeriod(period, nowUnix);
        var snapshotId = BuildSnapshotId("broadcast", channelId, period);

        // statsAbsValueAndPrev fields: current = aggregate over the Period, previous = aggregate over the
        // Previous_Period; AggregateAsync returns 0 for a range with no recorded metric (Requirement 2.8).
        var followers = await AbsValueAsync(channel, StatsMetricNames.Followers, period);

        // views/shares/reactions "per post" and "per story" are means, not period totals: divide the
        // interaction totals by the number of posts (resp. stories) published in the same range.
        var viewsPerPost = await PerItemValueAsync(channel, StatsMetricNames.Views, StatsMetricNames.Messages, period);
        var sharesPerPost = await PerItemValueAsync(channel, StatsMetricNames.Shares, StatsMetricNames.Messages, period);
        var reactionsPerPost = await PerItemValueAsync(channel, StatsMetricNames.Reactions, StatsMetricNames.Messages, period);
        var viewsPerStory = await PerItemValueAsync(channel, StatsMetricNames.StoryViews, StatsMetricNames.StoryPosts, period);
        var sharesPerStory = await PerItemValueAsync(channel, StatsMetricNames.StoryShares, StatsMetricNames.StoryPosts, period);
        var reactionsPerStory = await PerItemValueAsync(channel, StatsMetricNames.StoryReactions, StatsMetricNames.StoryPosts, period);

        // enabled_notifications: part = notifications-enabled count, total = subscriber count (Requirement 2.5).
        var notifyOn = await metricsStore.AggregateAsync(channel, StatsMetricNames.NotifyOn, period.MinDate, period.MaxDate);
        var muted = await metricsStore.AggregateAsync(channel, StatsMetricNames.Muted, period.MinDate, period.MaxDate);
        var subscriberCount = notifyOn + muted;

        // The notify gauges are only written when something moves them (a join/leave or a mute/unmute), so
        // a channel that has seen neither has no recorded pair at all. Clients divide part by total
        // without guarding it — DrKLO computes `part / total * 100f` — so a zero total renders as "NaN%".
        // Derive the pair from the live membership instead, which is what the recorder would have stored.
        if (subscriberCount <= 0)
        {
            (notifyOn, muted) = await ComputeLiveNotifyStateAsync(channelId);
            subscriberCount = notifyOn + muted;
        }

        var enabledNotifications = new TStatsPercentValue { Part = notifyOn, Total = subscriberCount };

        var recentPosts = await metricsStore.GetRecentPostInteractionsAsync(channelId);

        // The interactions graph zooms into hourly detail when hour-of-day view data exists.
        var viewsHourlyZoom = await BuildHourlyZoomSpecAsync(channel, StatsMetricNames.ViewsByHour, graphPeriod,
            "views", "Views", "primary", GraphKind.Line);

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
            GrowthGraph = await BuildGrowthGraphAsync(channel, StatsMetricNames.Followers, graphPeriod, "Growth", "primary", dark, snapshotId, nowUnix),
            FollowersGraph = await BuildGaugeSeriesGraphAsync(channel, StatsMetricNames.Followers, graphPeriod, "Followers", "primary", GraphKind.Line, dark, snapshotId, nowUnix),
            MuteGraph = await BuildGaugeSeriesGraphAsync(channel, StatsMetricNames.Muted, graphPeriod, "Muted", "secondary", GraphKind.Line, dark, snapshotId, nowUnix),
            TopHoursGraph = await BuildTopHoursGraphAsync(channel, StatsMetricNames.ViewsByHour, graphPeriod, "Views by hour", "tertiary", dark, snapshotId, nowUnix),
            InteractionsGraph = await BuildMultiSeriesGraphAsync(channel, graphPeriod,
                [(StatsMetricNames.Views, "Views", "primary"), (StatsMetricNames.Shares, "Shares", "secondary")],
                GraphKind.Line, dark, snapshotId, nowUnix, viewsHourlyZoom),
            // Instant View is not supported by this server, so IV interactions are genuinely zero.
            IvInteractionsGraph = await BuildMultiSeriesGraphAsync(channel, graphPeriod,
                [("iv_views", "IV views", "primary"), ("iv_shares", "IV shares", "secondary")],
                GraphKind.Line, dark, snapshotId, nowUnix),
            // View sources (URL/search/other channels) are not tracked; with no recorded categories the
            // Graph_Builder emits a statsGraphError for this slot.
            ViewsBySourceGraph = await BuildCategoryGraphAsync(channel, StatsMetricNames.Views, graphPeriod, GraphKind.StackedBar, dark, snapshotId, nowUnix),
            NewFollowersBySourceGraph = await BuildCategoryGraphAsync(channel, StatsMetricNames.JoinsBySource, graphPeriod, GraphKind.StackedBar, dark, snapshotId, nowUnix),
            LanguagesGraph = await BuildCategoryGraphAsync(channel, StatsMetricNames.JoinsByLanguage, graphPeriod, GraphKind.Pie, dark, snapshotId, nowUnix),
            ReactionsByEmotionGraph = await BuildCategoryGraphAsync(channel, StatsMetricNames.Reactions, graphPeriod, GraphKind.StackedBar, dark, snapshotId, nowUnix),
            StoryInteractionsGraph = await BuildMultiSeriesGraphAsync(channel, graphPeriod,
                [(StatsMetricNames.StoryViews, "Story views", "primary"), (StatsMetricNames.StoryShares, "Story shares", "secondary")],
                GraphKind.Line, dark, snapshotId, nowUnix),
            StoryReactionsByEmotionGraph = await BuildCategoryGraphAsync(channel, StatsMetricNames.StoryReactions, graphPeriod, GraphKind.StackedBar, dark, snapshotId, nowUnix),
            RecentPostsInteractions = new TVector<IPostInteractionCounters>(recentPosts.Select(ToPostInteractionCounters))
        };
    }

    public async Task<IMegagroupStats> GetMegagroupStatsAsync(IRequestInput input, long channelId, bool dark)
    {
        var channel = new StatsEntityKey(StatsEntityType.Channel, channelId, 0);
        var nowUnix = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var period = await metricsStore.GetPeriodAsync(channel, ReportingWindowDays);
        var graphPeriod = EffectiveGraphPeriod(period, nowUnix);
        var snapshotId = BuildSnapshotId("megagroup", channelId, period);

        var members = await AbsValueAsync(channel, StatsMetricNames.Members, period);
        var messages = await AbsValueAsync(channel, StatsMetricNames.Messages, period);
        var viewers = await AbsValueAsync(channel, StatsMetricNames.Viewers, period);
        var posters = await AbsValueAsync(channel, StatsMetricNames.Posters, period);

        // Nothing writes the distinct-viewer gauge, so "Viewing members" always read 0. The reading-history
        // read model records (reader, target peer, date), so distinct readers of this supergroup over the
        // range are a faithful stand-in.
        if (viewers is TStatsAbsValueAndPrev { Current: 0, Previous: 0 })
        {
            viewers = await DistinctViewersAsync(channelId, period);
        }

        // Nothing writes the distinct-poster gauge, so "Posting members" always read 0. The top-poster
        // breakdown is recorded per posting user id, so the distinct posters over a range are exactly its
        // category count — derive both the current and the previous period from it.
        if (posters is TStatsAbsValueAndPrev { Current: 0, Previous: 0 })
        {
            posters = await DistinctPostersAsync(channel, period);
        }

        var topEntities = await metricsStore.GetTopEntitiesAsync(channelId, period.MinDate, period.MaxDate);
        var users = await BuildUsersAsync(input, topEntities.UserIds);

        return new TMegagroupStats
        {
            Period = ToDateRange(period),
            Members = members,
            Messages = messages,
            Viewers = viewers,
            Posters = posters,
            GrowthGraph = await BuildGrowthGraphAsync(channel, StatsMetricNames.Members, graphPeriod, "Growth", "primary", dark, snapshotId, nowUnix),
            MembersGraph = await BuildGaugeSeriesGraphAsync(channel, StatsMetricNames.Members, graphPeriod, "Members", "primary", GraphKind.Line, dark, snapshotId, nowUnix),
            NewMembersBySourceGraph = await BuildCategoryGraphAsync(channel, StatsMetricNames.JoinsBySource, graphPeriod, GraphKind.StackedBar, dark, snapshotId, nowUnix),
            LanguagesGraph = await BuildCategoryGraphAsync(channel, StatsMetricNames.JoinsByLanguage, graphPeriod, GraphKind.Pie, dark, snapshotId, nowUnix),
            MessagesGraph = await BuildSeriesGraphAsync(channel, StatsMetricNames.Messages, graphPeriod, "Messages", "secondary", GraphKind.StackedBar, dark, snapshotId, nowUnix,
                await BuildHourlyZoomSpecAsync(channel, StatsMetricNames.MessagesByHour, graphPeriod, "messages", "Messages", "secondary", GraphKind.StackedBar)),
            // actions_graph is parsed as a two-line chart by official clients: messages posted vs
            // membership changes (a single series would fault on the second line).
            ActionsGraph = await BuildMultiSeriesGraphAsync(channel, graphPeriod,
                [(StatsMetricNames.Messages, "Messages", "primary"), (StatsMetricNames.Actions, "Actions", "secondary")],
                GraphKind.Line, dark, snapshotId, nowUnix),
            TopHoursGraph = await BuildTopHoursGraphAsync(channel, StatsMetricNames.MessagesByHour, graphPeriod, "Activity by hour", "primary", dark, snapshotId, nowUnix),
            WeekdaysGraph = await BuildCategoryGraphAsync(channel, StatsMetricNames.MessagesByWeekday, graphPeriod, GraphKind.StackedBar, dark, snapshotId, nowUnix),
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
    /// Period (Requirements 4.1, 4.3). An item with no recorded metric yields a Period-covering
    /// zero-filled <c>views_graph</c> and a <c>statsGraphError</c> for the category graph
    /// (Requirement 4.4, enforced by the Graph_Builder). When the
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
        var graphPeriod = EffectiveGraphPeriod(period, nowUnix);
        var snapshotId = BuildItemSnapshotId("message", channelId, msgId, period);

        return new TMessageStats
        {
            ViewsGraph = await BuildSeriesGraphAsync(entity, StatsMetricNames.Views, graphPeriod, "Views", "primary", GraphKind.Line, dark, snapshotId, nowUnix),
            ReactionsByEmotionGraph = await BuildCategoryGraphAsync(entity, StatsMetricNames.Reactions, graphPeriod, GraphKind.StackedBar, dark, snapshotId, nowUnix)
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
        var graphPeriod = EffectiveGraphPeriod(period, nowUnix);
        var snapshotId = BuildItemSnapshotId("story", peer.PeerId, storyId, period);

        return new TStoryStats
        {
            ViewsGraph = await BuildSeriesGraphAsync(entity, StatsMetricNames.Views, graphPeriod, "Views", "primary", GraphKind.Line, dark, snapshotId, nowUnix),
            ReactionsByEmotionGraph = await BuildCategoryGraphAsync(entity, StatsMetricNames.Reactions, graphPeriod, GraphKind.StackedBar, dark, snapshotId, nowUnix)
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
    /// Counts distinct users who read messages in the supergroup over the Period and the Previous_Period,
    /// from the reading-history read model. Used for the supergroup "Viewing members" figure, which has no
    /// gauge of its own (the view event carries only a count, never the viewer's identity).
    /// </summary>
    private async Task<IStatsAbsValueAndPrev> DistinctViewersAsync(long channelId, StatsDateRange period)
    {
        var current = await DistinctReaderCountAsync(channelId, period.MinDate, period.MaxDate);

        var previousMin = period.MinDate - (period.MaxDate - period.MinDate);
        var previous = await DistinctReaderCountAsync(channelId, previousMin, period.MinDate);

        return new TStatsAbsValueAndPrev { Current = current, Previous = previous };
    }

    private async Task<long> DistinctReaderCountAsync(long channelId, int minDay, int maxDay)
    {
        if (maxDay < minDay)
        {
            return 0;
        }

        // Reading history is stamped with a Unix second timestamp; the range bounds are day-aligned, so
        // include the whole of the final day.
        var filter = Builders<BsonDocument>.Filter.Eq("TargetPeerId", channelId)
                     & Builders<BsonDocument>.Filter.Gte("Date", minDay)
                     & Builders<BsonDocument>.Filter.Lt("Date", maxDay + SecondsPerDay);

        // "Viewing members" is one figure on a screen full of them: a reading-history query that fails must
        // degrade to 0 rather than fail the whole megagroup result.
        try
        {
            var cursor = await mongoDatabase.GetCollection<BsonDocument>(ReadingHistoryCollectionName)
                .DistinctAsync<BsonValue>("ReaderPeerId", filter);
            if (cursor == null)
            {
                return 0;
            }

            var readers = await cursor.ToListAsync();
            return readers?.Count ?? 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Counts distinct posting users over the Period and the Previous_Period from the
    /// <c>top_poster_messages</c> breakdown, whose categories are the posting user ids. Used for the
    /// supergroup "Posting members" figure, which has no gauge of its own.
    /// </summary>
    private async Task<IStatsAbsValueAndPrev> DistinctPostersAsync(StatsEntityKey entity, StatsDateRange period)
    {
        var current = await DistinctCategoryCountAsync(entity, period.MinDate, period.MaxDate);

        var previousMin = period.MinDate - (period.MaxDate - period.MinDate);
        var previous = await DistinctCategoryCountAsync(entity, previousMin, period.MinDate);

        return new TStatsAbsValueAndPrev { Current = current, Previous = previous };
    }

    private async Task<long> DistinctCategoryCountAsync(StatsEntityKey entity, int minDay, int maxDay)
    {
        // As with the viewer count, one overview figure must not be able to fail the whole result.
        try
        {
            var categories = await metricsStore.GetCategorySeriesAsync(
                entity, StatsMetricNames.TopPosterMessages, minDay, maxDay);

            // A category is present with all-zero points when the breakdown was recorded but the user
            // posted nothing in this range; only count users who actually posted.
            return categories?.Count(c => c.Points.Any(p => p.Value > 0)) ?? 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Recomputes the <c>notify_on</c>/<c>muted</c> pair from live state, for a channel whose gauges were
    /// never recorded because it has seen no join/leave and no mute/unmute since stats ingestion started.
    ///
    /// <para>Mirrors <c>NotifyStateRecorder</c>: the muted count comes from the per-user notify-settings
    /// read model, which keeps documents for users who left (or only previewed) the channel, so it is
    /// clamped to the current participant count and <c>notify_on = participants - muted</c>.</para>
    ///
    /// <para>Returns <c>(0, 0)</c> when the channel is unknown or reports no participants; the caller then
    /// emits <c>part = total = 0</c>, which is what the official server sends for an empty channel.</para>
    /// </summary>
    private async Task<(long NotifyOn, long Muted)> ComputeLiveNotifyStateAsync(long channelId)
    {
        // The fallback is a convenience over live state; if either lookup fails, fall back to {0,0} rather
        // than failing the whole broadcast result over one overview cell.
        try
        {
            var channelReadModel = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(channelId));
            var participants = (long)Math.Max(0, channelReadModel?.ParticipantsCount ?? 0);
            if (participants == 0)
            {
                return (0, 0);
            }

            var now = DateTime.UtcNow.ToTimestamp();
            var filter = Builders<BsonDocument>.Filter.Eq("PeerId", channelId)
                         & Builders<BsonDocument>.Filter.Eq("PeerType", (int)PeerType.Channel)
                         & (Builders<BsonDocument>.Filter.Eq("NotifySettings.Silent", true)
                            | Builders<BsonDocument>.Filter.Gt("NotifySettings.MuteUntil", now));

            var muted = await mongoDatabase.GetCollection<BsonDocument>(NotifySettingsCollectionName)
                .CountDocumentsAsync(filter);

            muted = Math.Clamp(muted, 0, participants);
            return (participants - muted, muted);
        }
        catch (Exception)
        {
            return (0, 0);
        }
    }

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
    /// Builds a <c>statsAbsValueAndPrev</c> for a per-item mean: the total of
    /// <paramref name="totalMetric"/> divided by the number of items counted by
    /// <paramref name="itemCountMetric"/>, over the Period and the Previous_Period respectively. A range
    /// without items yields <c>0</c> rather than a division by zero.
    /// </summary>
    private async Task<IStatsAbsValueAndPrev> PerItemValueAsync(StatsEntityKey entity, string totalMetric,
        string itemCountMetric, StatsDateRange period)
    {
        var previousMin = period.MinDate - (period.MaxDate - period.MinDate);

        var currentTotal = await metricsStore.AggregateAsync(entity, totalMetric, period.MinDate, period.MaxDate);
        var currentItems = await metricsStore.AggregateAsync(entity, itemCountMetric, period.MinDate, period.MaxDate);
        var previousTotal = await metricsStore.AggregateAsync(entity, totalMetric, previousMin, period.MinDate);
        var previousItems = await metricsStore.AggregateAsync(entity, itemCountMetric, previousMin, period.MinDate);

        return new TStatsAbsValueAndPrev
        {
            Current = currentItems > 0 ? (double)currentTotal / currentItems : 0,
            Previous = previousItems > 0 ? (double)previousTotal / previousItems : 0
        };
    }

    /// <summary>
    /// The period used for graph x-axes. For an entity with no recorded metrics
    /// (<see cref="IMetricsStore.GetPeriodAsync"/> yields <c>{0,0}</c>) this falls back to the reporting
    /// window ending at the current UTC day so zero-filled graphs carry current dates; the response
    /// <c>period</c> itself keeps the store's value (Requirement 10.4).
    /// </summary>
    private StatsDateRange EffectiveGraphPeriod(StatsDateRange period, int nowUnix)
    {
        if (period.MaxDate > 0)
        {
            return period;
        }

        var today = nowUnix - nowUnix % SecondsPerDay;
        var window = Math.Clamp(ReportingWindowDays, 1, 365);
        return new StatsDateRange(today - window * SecondsPerDay, today);
    }

    /// <summary>Enumerates every UTC day key of the Period, inclusive on both ends.</summary>
    private static List<int> EnumeratePeriodDays(int minDay, int maxDay)
    {
        var days = new List<int>();
        // long iteration variable: an int would wrap around near int.MaxValue (year 2038) and loop forever.
        for (long day = minDay; day <= maxDay; day += SecondsPerDay)
        {
            days.Add((int)day);
        }

        return days;
    }

    /// <summary>
    /// Builds a single-series <c>statsGraph</c> from the per-day series of <paramref name="metric"/>,
    /// zero-filled across the whole Period so the x-axis always carries at least 2 points (days without a
    /// recorded value contribute <c>0</c>); client chart parsers crash on shorter axes.
    /// </summary>
    private async Task<IStatsGraph> BuildSeriesGraphAsync(StatsEntityKey entity, string metric, StatsDateRange period,
        string seriesName, string colorKey, GraphKind kind, bool dark, string snapshotId, int nowUnix,
        GraphSpec? zoom = null)
    {
        var days = EnumeratePeriodDays(period.MinDate, period.MaxDate);
        var values = await GetCounterDailyValuesAsync(entity, metric, period, days);
        var xAxis = days.Select(d => d * MillisPerSecond).ToList();
        var series = new[] { new GraphSeries(metric, seriesName, colorKey, values) };

        var spec = new GraphSpec(kind, xAxis, series, zoom);
        return await graphBuilder.BuildInlineAsync(spec, dark, snapshotId, nowUnix);
    }

    /// <summary>
    /// Builds a multi-series counter <c>statsGraph</c> (e.g. the interactions graph's views+shares pair),
    /// each series zero-filled across the whole Period.
    /// </summary>
    private async Task<IStatsGraph> BuildMultiSeriesGraphAsync(StatsEntityKey entity, StatsDateRange period,
        IReadOnlyList<(string Metric, string Name, string ColorKey)> seriesDefs, GraphKind kind, bool dark,
        string snapshotId, int nowUnix, GraphSpec? zoom = null)
    {
        var days = EnumeratePeriodDays(period.MinDate, period.MaxDate);
        var xAxis = days.Select(d => d * MillisPerSecond).ToList();

        var series = new List<GraphSeries>(seriesDefs.Count);
        foreach (var (metric, name, colorKey) in seriesDefs)
        {
            var values = await GetCounterDailyValuesAsync(entity, metric, period, days);
            series.Add(new GraphSeries(metric, name, colorKey, values));
        }

        var spec = new GraphSpec(kind, xAxis, series, zoom);
        return await graphBuilder.BuildInlineAsync(spec, dark, snapshotId, nowUnix);
    }

    private async Task<List<long>> GetCounterDailyValuesAsync(StatsEntityKey entity, string metric,
        StatsDateRange period, List<int> days)
    {
        var points = await metricsStore.GetSeriesAsync(entity, metric, period.MinDate, period.MaxDate);

        var byDay = new Dictionary<int, long>(points.Count);
        foreach (var point in points)
        {
            byDay[point.UtcDay] = byDay.GetValueOrDefault(point.UtcDay) + point.Value;
        }

        return days.Select(d => byDay.GetValueOrDefault(d)).ToList();
    }

    /// <summary>
    /// Builds a single-series <c>statsGraph</c> for a gauge metric (followers/members/muted). Unlike
    /// counters, days without a recorded snapshot carry the last known value forward (a quiet day does not
    /// drop the follower count to zero); days before the first snapshot are <c>0</c>.
    /// </summary>
    private async Task<IStatsGraph> BuildGaugeSeriesGraphAsync(StatsEntityKey entity, string metric,
        StatsDateRange period, string seriesName, string colorKey, GraphKind kind, bool dark, string snapshotId,
        int nowUnix)
    {
        var days = EnumeratePeriodDays(period.MinDate, period.MaxDate);
        var values = await GetGaugeDailyValuesAsync(entity, metric, period.MinDate, days);
        var xAxis = days.Select(d => d * MillisPerSecond).ToList();
        var series = new[] { new GraphSeries(metric, seriesName, colorKey, values) };

        return await graphBuilder.BuildInlineAsync(new GraphSpec(kind, xAxis, series), dark, snapshotId, nowUnix);
    }

    /// <summary>
    /// Builds the growth graph: the per-day delta of a gauge metric (how many followers/members were
    /// gained or lost each day), seeded from the last snapshot before the Period.
    /// </summary>
    private async Task<IStatsGraph> BuildGrowthGraphAsync(StatsEntityKey entity, string metric,
        StatsDateRange period, string seriesName, string colorKey, bool dark, string snapshotId, int nowUnix)
    {
        // One extra day in front so the first window day has a predecessor to diff against.
        var days = EnumeratePeriodDays(period.MinDate - SecondsPerDay, period.MaxDate);
        var values = await GetGaugeDailyValuesAsync(entity, metric, period.MinDate - SecondsPerDay, days);

        var xAxis = new List<long>(days.Count - 1);
        var deltas = new List<long>(days.Count - 1);
        for (var i = 1; i < days.Count; i++)
        {
            xAxis.Add(days[i] * MillisPerSecond);
            deltas.Add(values[i] - values[i - 1]);
        }

        var series = new[] { new GraphSeries("growth", seriesName, colorKey, deltas) };
        return await graphBuilder.BuildInlineAsync(new GraphSpec(GraphKind.Line, xAxis, series), dark, snapshotId, nowUnix);
    }

    /// <summary>
    /// Per-day absolute values of a gauge over <paramref name="days"/>: the recorded snapshot where one
    /// exists, otherwise the last known value carried forward (seeded from history before
    /// <paramref name="minDay"/>).
    /// </summary>
    private async Task<List<long>> GetGaugeDailyValuesAsync(StatsEntityKey entity, string metric, int minDay,
        List<int> days)
    {
        // Include all history up to the window end so the forward-fill has a seed value.
        var points = await metricsStore.GetSeriesAsync(entity, metric, 0, days.Count > 0 ? days[^1] : minDay);

        var byDay = new Dictionary<int, long>(points.Count);
        long seed = 0;
        foreach (var point in points)
        {
            byDay[point.UtcDay] = point.Value;
            if (point.UtcDay < minDay)
            {
                seed = point.Value;
            }
        }

        var values = new List<long>(days.Count);
        var last = seed;
        foreach (var day in days)
        {
            if (byDay.TryGetValue(day, out var value))
            {
                last = value;
            }

            values.Add(last);
        }

        return values;
    }

    /// <summary>
    /// Builds the top-hours graph: 24 hour-of-day buckets aggregated from <paramref name="hourMetric"/>'s
    /// breakdown over the Period. The x axis carries the raw hour indices <c>0..23</c> — clients detect the
    /// hour format from a unit x step (<c>timeStep == 1</c>) and label the axis <c>"00:00".."23:00"</c>;
    /// millisecond offsets would be rendered as 1970 dates instead.
    /// </summary>
    private async Task<IStatsGraph> BuildTopHoursGraphAsync(StatsEntityKey entity, string hourMetric,
        StatsDateRange period, string seriesName, string colorKey, bool dark, string snapshotId, int nowUnix)
    {
        var totals = await metricsStore.GetBreakdownTotalsAsync(entity, hourMetric, period.MinDate, period.MaxDate);

        var xAxis = new List<long>(24);
        var values = new List<long>(24);
        for (var hour = 0; hour < 24; hour++)
        {
            xAxis.Add(hour);
            values.Add(totals.GetValueOrDefault(hour.ToString()));
        }

        var series = new[] { new GraphSeries("top_hours", seriesName, colorKey, values) };
        return await graphBuilder.BuildInlineAsync(new GraphSpec(GraphKind.Line, xAxis, series), dark, snapshotId, nowUnix);
    }

    /// <summary>
    /// Builds the hourly-detail zoom spec for a counter that records an hour-of-day breakdown: one point
    /// per hour across the whole Period (<c>days × 24</c>). Returns <see langword="null"/> when no hourly
    /// data exists — the graph then carries no <c>zoom_token</c>.
    /// </summary>
    private async Task<GraphSpec?> BuildHourlyZoomSpecAsync(StatsEntityKey entity, string hourMetric,
        StatsDateRange period, string seriesId, string seriesName, string colorKey, GraphKind kind)
    {
        var categorySeries = await metricsStore.GetCategorySeriesAsync(entity, hourMetric, period.MinDate, period.MaxDate);
        if (categorySeries.Count == 0)
        {
            return null;
        }

        var byDayHour = new Dictionary<(int Day, int Hour), long>();
        foreach (var category in categorySeries)
        {
            if (!int.TryParse(category.Category, out var hour) || hour is < 0 or > 23)
            {
                continue;
            }

            foreach (var point in category.Points)
            {
                byDayHour[(point.UtcDay, hour)] = byDayHour.GetValueOrDefault((point.UtcDay, hour)) + point.Value;
            }
        }

        if (byDayHour.Count == 0)
        {
            return null;
        }

        var days = EnumeratePeriodDays(period.MinDate, period.MaxDate);
        var xAxis = new List<long>(days.Count * 24);
        var values = new List<long>(days.Count * 24);
        foreach (var day in days)
        {
            for (var hour = 0; hour < 24; hour++)
            {
                xAxis.Add(day * MillisPerSecond + hour * MillisPerHour);
                values.Add(byDayHour.GetValueOrDefault((day, hour)));
            }
        }

        var series = new[] { new GraphSeries(seriesId, seriesName, colorKey, values) };
        return new GraphSpec(kind, xAxis, series);
    }

    /// <summary>
    /// Builds a multi-series <c>statsGraph</c> from the per-category per-day series of
    /// <paramref name="metric"/>, zero-filled across the whole Period (missing days contribute <c>0</c>).
    /// With no recorded categories the spec has zero data series and the Graph_Builder emits a
    /// <c>statsGraphError</c> — category names cannot be invented.
    /// </summary>
    private async Task<IStatsGraph> BuildCategoryGraphAsync(StatsEntityKey entity, string metric, StatsDateRange period,
        GraphKind kind, bool dark, string snapshotId, int nowUnix)
    {
        var categorySeries = await metricsStore.GetCategorySeriesAsync(entity, metric, period.MinDate, period.MaxDate);

        var days = EnumeratePeriodDays(period.MinDate, period.MaxDate);
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
                if (dayIndex.TryGetValue(point.UtcDay, out var index))
                {
                    values[index] += point.Value;
                }
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
