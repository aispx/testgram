using System.Text.Json.Nodes;
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

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Pins the assembled broadcast statistics to the payload real Telegram serves, captured from
/// <c>stats.getBroadcastStats</c> on a production channel the test author administers (@xieworld, 149
/// followers, stats_dc=2).
///
/// <para>The reference established several things this server had wrong:</para>
/// <list type="bullet">
///   <item><c>growth_graph</c> is the ABSOLUTE follower count — one blue line named "Total followers".
///   <c>followers_graph</c> is the per-day churn PAIR, "Joined" (green) and "Left" (red). This server had
///   the two the wrong way round.</item>
///   <item>Colors travel as <c>NAME#RRGGBB</c> (e.g. <c>BLUE#007AFF</c>), not a bare hex: the Android
///   client splits on <c>(.*)(#.*)</c> and maps the prefix to the theme key
///   <c>statisticChartLine_&lt;name&gt;</c>.</item>
///   <item>The interaction pairs are <c>step</c> series, views blue and shares golden.</item>
///   <item>The payload carries <c>title</c>, <c>hidden</c>, <c>subchart</c>, <c>strokeWidth</c>, the four
///   formatter fields, <c>stacked</c> and <c>y_scaled</c> — this server emitted only the bare four keys.</item>
/// </list>
/// </summary>
public class RealTelegramGraphShapeTests
{
    private const long ChannelId = 800_000_002_001;
    private const int SecondsPerDay = 86_400;

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

    private static JsonObject Payload(IStatsGraph graph) =>
        (JsonObject)JsonNode.Parse(graph.ShouldBeOfType<TStatsGraph>().Json.Data)!;

    private static Dictionary<string, string> Map(JsonObject root, string key) =>
        ((JsonObject)root[key]!).ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>());

    private static async Task<TBroadcastStats> BuildStatsAsync(IMongoDatabase database)
    {
        var store = new MetricsStore(database);
        var entity = new StatsEntityKey(StatsEntityType.Channel, ChannelId, 0);
        var today = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / SecondsPerDay) * SecondsPerDay;
        var yesterday = today - SecondsPerDay;

        foreach (var day in new[] { yesterday, today })
        {
            await store.RecordAsync(entity, StatsMetricNames.Views, day, 11);
            await store.RecordAsync(entity, StatsMetricNames.Shares, day, 4);
            await store.RecordAsync(entity, StatsMetricNames.Messages, day, 3);
        }

        // A gauge that moves, so the churn pair has something to draw.
        await store.RecordAsync(entity, StatsMetricNames.Followers, yesterday, 3);
        await store.RecordAsync(entity, StatsMetricNames.Followers, today, 5);

        return (TBroadcastStats)await CreateService(database, participantsCount: 5)
            .GetBroadcastStatsAsync(CreateInput(), ChannelId, dark: false);
    }

    [RequiresMongoDbFact]
    public async Task Growth_graph_is_the_absolute_follower_count()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var stats = await BuildStatsAsync(mongo.Database);

        var root = Payload(stats.GrowthGraph);
        var names = Map(root, "names");

        names.Values.ShouldContain("Total followers");
        names.Count.ShouldBe(1, "growth_graph is a single line on production");
        Map(root, "colors").Values.ShouldAllBe(c => c == "BLUE#007AFF");

        // The values are the gauge itself, not its delta.
        var column = (JsonArray)((JsonArray)root["columns"]!)[1]!;
        column[^1]!.GetValue<long>().ShouldBe(5);
    }

    [RequiresMongoDbFact]
    public async Task Followers_graph_is_the_joined_left_churn_pair()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var stats = await BuildStatsAsync(mongo.Database);

        var root = Payload(stats.FollowersGraph);
        var names = Map(root, "names");
        var colors = Map(root, "colors");

        names["joined"].ShouldBe("Joined");
        names["left"].ShouldBe("Left");
        colors["joined"].ShouldBe("GREEN#34C759");
        colors["left"].ShouldBe("RED#FF3B30");

        // Followers went 3 -> 5, so the last day records 2 joins and no leaves.
        var columns = (JsonArray)root["columns"]!;
        var joined = (JsonArray)columns[1]!;
        var left = (JsonArray)columns[2]!;
        joined[^1]!.GetValue<long>().ShouldBe(2);
        left[^1]!.GetValue<long>().ShouldBe(0);
    }

    [RequiresMongoDbFact]
    public async Task Interactions_graph_uses_step_series_coloured_blue_and_golden()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var stats = await BuildStatsAsync(mongo.Database);

        var root = Payload(stats.InteractionsGraph);

        Map(root, "types").Where(kv => kv.Key != "x").Select(kv => kv.Value)
            .ShouldAllBe(t => t == "step");
        Map(root, "names").Values.ShouldBe(new[] { "Views", "Shares" }, ignoreOrder: true);

        var colors = Map(root, "colors");
        colors[StatsMetricNames.Views].ShouldBe("BLUE#007AFF");
        colors[StatsMetricNames.Shares].ShouldBe("GOLDEN#FFCC00");
    }

    [RequiresMongoDbFact]
    public async Task Every_graph_carries_the_fields_production_sends()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var stats = await BuildStatsAsync(mongo.Database);

        var root = Payload(stats.GrowthGraph);

        foreach (var key in new[]
                 {
                     "title", "columns", "types", "names", "colors", "hidden", "subchart", "strokeWidth",
                     "xTickFormatter", "xTooltipFormatter", "xRangeFormatter", "yTickFormatter",
                     "yTooltipFormatter", "tooltipSort", "stacked", "y_scaled",
                 })
        {
            root.ContainsKey(key).ShouldBeTrue($"production sends '{key}'");
        }

        root["xTickFormatter"]!.GetValue<string>().ShouldBe("statsFormat('day')");
        root["strokeWidth"]!.GetValue<int>().ShouldBe(2);
        ((JsonObject)root["subchart"]!)["show"]!.GetValue<bool>().ShouldBeTrue();
    }

    [RequiresMongoDbFact]
    public async Task A_paired_graph_is_y_scaled_and_a_single_line_is_not()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var stats = await BuildStatsAsync(mongo.Database);

        // views vs shares live on different scales, so they get independent y axes.
        Payload(stats.InteractionsGraph)["y_scaled"]!.GetValue<bool>().ShouldBeTrue();
        Payload(stats.GrowthGraph)["y_scaled"]!.GetValue<bool>().ShouldBeFalse();
    }

    [RequiresMongoDbFact]
    public async Task Mute_graph_is_the_green_unmuted_series()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new MetricsStore(mongo.Database);
        var entity = new StatsEntityKey(StatsEntityType.Channel, ChannelId, 0);
        var today = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / SecondsPerDay) * SecondsPerDay;
        await store.RecordAsync(entity, StatsMetricNames.NotifyOn, today - SecondsPerDay, 40);
        await store.RecordAsync(entity, StatsMetricNames.NotifyOn, today, 43);

        var stats = (TBroadcastStats)await CreateService(mongo.Database, participantsCount: 149)
            .GetBroadcastStatsAsync(CreateInput(), ChannelId, dark: false);

        var root = Payload(stats.MuteGraph);
        Map(root, "names").Values.ShouldContain("Unmuted");
        Map(root, "colors").Values.ShouldAllBe(c => c == "GREEN#34C759");
    }
}
