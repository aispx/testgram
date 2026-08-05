using EventFlow.Queries;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Stats;
using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Schema.Stats;
using Xunit.Abstractions;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Diagnostic sweep over the assembled broadcast statistics: builds the real result for a channel with
/// this server's actual data shape and reports which slots a client would render, which come back as
/// <c>statsGraphError</c>, and whether any overview figure would format as NaN or Infinity.
///
/// <para>Not a pass/fail contract for individual slots (a slot can legitimately be empty when nothing is
/// tracked) — the assertions only pin the invariants that break a client: no percentage may be NaN or
/// infinite, and no graph may reach the client with a shape its parsers crash on.</para>
/// </summary>
public class BroadcastStatsSlotDiagnosticsTests(ITestOutputHelper output)
{
    private const long ChannelId = 800_000_002_001;

    private static IRequestInput CreateInput()
    {
        var input = new Mock<IRequestInput>(MockBehavior.Loose);
        input.SetupGet(x => x.UserId).Returns(2_010_001);
        return input.Object;
    }

    private static StatsService CreateService(IMongoDatabase database, int participantsCount)
    {
        var channel = new Mock<IChannelReadModel>(MockBehavior.Loose);
        channel.SetupGet(x => x.ChannelId).Returns(ChannelId);
        channel.SetupGet(x => x.ParticipantsCount).Returns(participantsCount);

        var queryProcessor = new Mock<IQueryProcessor>(MockBehavior.Loose);
        queryProcessor
            .Setup(x => x.ProcessAsync(It.IsAny<GetChannelByIdQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(channel.Object);

        return new StatsService(
            new MetricsStore(database),
            new GraphBuilder(new FakeAsyncGraphStore()),
            new Mock<IUserConverterService>(MockBehavior.Loose).Object,
            new Mock<IChatConverterService>(MockBehavior.Loose).Object,
            new Mock<IPublicForwardStore>(MockBehavior.Loose).Object,
            new Mock<IAsyncGraphStore>(MockBehavior.Loose).Object,
            new Mock<IMessageConverterService>(MockBehavior.Loose).Object,
            new Mock<IMessageAppService>(MockBehavior.Loose).Object,
            queryProcessor.Object,
            database,
            StatsTestOptions.Create());
    }

    private static string Describe(IStatsGraph graph) => graph switch
    {
        TStatsGraph g => $"statsGraph ({g.Json.Data.Length} bytes"
                         + (string.IsNullOrEmpty(g.ZoomToken) ? ")" : ", zoomable)"),
        TStatsGraphError e => $"statsGraphError: {e.Error}",
        TStatsGraphAsync => "statsGraphAsync",
        _ => graph.GetType().Name,
    };

    /// <summary>The client's formula for a statsPercentValue cell: part / total * 100.</summary>
    private static double ClientPercent(IStatsPercentValue value)
    {
        var percent = (TStatsPercentValue)value;
        return percent.Part / percent.Total * 100d;
    }

    /// <summary>The client's formula for a statsAbsValueAndPrev delta cell.</summary>
    private static double ClientDelta(IStatsAbsValueAndPrev value)
    {
        var abs = (TStatsAbsValueAndPrev)value;
        return abs.Previous == 0 ? 0 : Math.Abs((abs.Current - abs.Previous) / abs.Previous * 100d);
    }

    [RequiresMongoDbFact]
    public async Task Every_broadcast_slot_is_either_renderable_or_an_explicit_error()
    {
        using var mongo = EmbeddedMongoServer.Start();

        // A channel with views recorded but no shares, and no notify gauges — the shape that produced both
        // the divide-by-zero chart crash and the NaN% notifications cell on this server.
        var store = new MetricsStore(mongo.Database);
        var entity = new StatsEntityKey(StatsEntityType.Channel, ChannelId, 0);
        var today = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 86_400) * 86_400;
        await store.RecordAsync(entity, StatsMetricNames.Views, today, 11);
        await store.RecordAsync(entity, StatsMetricNames.Messages, today, 3);
        await store.RecordAsync(entity, StatsMetricNames.Followers, today, 5);

        var stats = (TBroadcastStats)await CreateService(mongo.Database, participantsCount: 5)
            .GetBroadcastStatsAsync(CreateInput(), ChannelId, dark: false);

        var graphs = new (string Name, IStatsGraph Graph)[]
        {
            ("growth_graph", stats.GrowthGraph),
            ("followers_graph", stats.FollowersGraph),
            ("mute_graph", stats.MuteGraph),
            ("top_hours_graph", stats.TopHoursGraph),
            ("interactions_graph", stats.InteractionsGraph),
            ("iv_interactions_graph", stats.IvInteractionsGraph),
            ("views_by_source_graph", stats.ViewsBySourceGraph),
            ("new_followers_by_source_graph", stats.NewFollowersBySourceGraph),
            ("languages_graph", stats.LanguagesGraph),
            ("reactions_by_emotion_graph", stats.ReactionsByEmotionGraph),
            ("story_interactions_graph", stats.StoryInteractionsGraph),
            ("story_reactions_by_emotion_graph", stats.StoryReactionsByEmotionGraph),
        };

        output.WriteLine("--- graphs ---");
        foreach (var (name, graph) in graphs)
        {
            output.WriteLine($"{name,-32} {Describe(graph)}");
            // Every slot must be populated: a null graph field serializes as a crash on the client.
            graph.ShouldNotBeNull($"{name} must not be null");
        }

        var percentages = new (string Name, double Value)[]
        {
            ("enabled_notifications", ClientPercent(stats.EnabledNotifications)),
            ("followers delta", ClientDelta(stats.Followers)),
            ("views_per_post delta", ClientDelta(stats.ViewsPerPost)),
            ("shares_per_post delta", ClientDelta(stats.SharesPerPost)),
            ("reactions_per_post delta", ClientDelta(stats.ReactionsPerPost)),
            ("views_per_story delta", ClientDelta(stats.ViewsPerStory)),
            ("shares_per_story delta", ClientDelta(stats.SharesPerStory)),
            ("reactions_per_story delta", ClientDelta(stats.ReactionsPerStory)),
        };

        output.WriteLine("--- overview figures (as the client formats them) ---");
        foreach (var (name, value) in percentages)
        {
            output.WriteLine($"{name,-32} {value}");
            double.IsNaN(value).ShouldBeFalse($"{name} formats as NaN%");
            double.IsInfinity(value).ShouldBeFalse($"{name} formats as Infinity%");
        }

        // The pair graphs must not be served with a zero series: DoubleLinearChartData divides by the
        // series maximum. shares is untracked here, so interactions_graph has to be an error slot.
        stats.InteractionsGraph.ShouldBeOfType<TStatsGraphError>();
        stats.IvInteractionsGraph.ShouldBeOfType<TStatsGraphError>();

        // Everything the channel does have data for is served for real. growth_graph is the absolute
        // follower count, so a single recorded snapshot already fills it.
        stats.GrowthGraph.ShouldBeOfType<TStatsGraph>();
        stats.EnabledNotifications.ShouldBeOfType<TStatsPercentValue>().Total.ShouldBe(5d);
    }

    [RequiresMongoDbFact]
    public async Task A_channel_with_a_full_data_set_serves_every_graph_for_real()
    {
        using var mongo = EmbeddedMongoServer.Start();

        var store = new MetricsStore(mongo.Database);
        var entity = new StatsEntityKey(StatsEntityType.Channel, ChannelId, 0);
        var today = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 86_400) * 86_400;
        var yesterday = today - 86_400;

        foreach (var day in new[] { yesterday, today })
        {
            await store.RecordAsync(entity, StatsMetricNames.Views, day, 11);
            await store.RecordAsync(entity, StatsMetricNames.Shares, day, 4);
            await store.RecordAsync(entity, StatsMetricNames.Messages, day, 3);
            await store.RecordAsync(entity, StatsMetricNames.NotifyOn, day, 4);
            await store.RecordAsync(entity, StatsMetricNames.Muted, day, 1);
        }

        // followers is a gauge: give it a day-over-day movement so the churn pair ("Joined"/"Left") has
        // something to draw. A flat count legitimately yields an empty churn graph.
        await store.RecordAsync(entity, StatsMetricNames.Followers, yesterday, 3);
        await store.RecordAsync(entity, StatsMetricNames.Followers, today, 5);

        var stats = (TBroadcastStats)await CreateService(mongo.Database, participantsCount: 5)
            .GetBroadcastStatsAsync(CreateInput(), ChannelId, dark: false);

        // With both series populated the interactions pair is a real chart again.
        stats.InteractionsGraph.ShouldBeOfType<TStatsGraph>();
        // growth_graph is the absolute count; followers_graph is the Joined/Left churn pair.
        stats.GrowthGraph.ShouldBeOfType<TStatsGraph>();
        stats.FollowersGraph.ShouldBeOfType<TStatsGraph>();
        stats.MuteGraph.ShouldBeOfType<TStatsGraph>();

        var percent = stats.EnabledNotifications.ShouldBeOfType<TStatsPercentValue>();
        percent.Part.ShouldBe(4d);
        percent.Total.ShouldBe(5d);
        ClientPercent(percent).ShouldBe(80d);
    }
}
