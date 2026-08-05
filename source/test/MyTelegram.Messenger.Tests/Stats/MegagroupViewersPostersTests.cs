using EventFlow.Queries;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Stats;
using MyTelegram.Schema;
using MyTelegram.Schema.Stats;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Regression tests for the supergroup overview's "Viewing members" and "Posting members" figures, which
/// always read 0 because nothing ever wrote the <c>viewers</c>/<c>posters</c> gauges.
///
/// <para>Neither can be recorded straightforwardly: the view event
/// (<c>MessageViewsIncrementedEvent2</c>) carries only a count, never the viewer's identity. Both are
/// therefore derived on read from data the server does record —</para>
/// <list type="bullet">
///   <item><c>posters</c> from the <c>top_poster_messages</c> breakdown, whose categories are the posting
///   user ids;</item>
///   <item><c>viewers</c> from the reading-history read model, which stores
///   <c>(ReaderPeerId, TargetPeerId, Date)</c> per read.</item>
/// </list>
///
/// <para>A recorded gauge, should ingestion ever start writing one, still takes precedence.</para>
/// </summary>
public class MegagroupViewersPostersTests
{
    private const long ChannelId = 800_000_000_001;
    private const int SecondsPerDay = 86_400;

    private static IRequestInput CreateInput()
    {
        var input = new Mock<IRequestInput>(MockBehavior.Loose);
        input.SetupGet(x => x.UserId).Returns(2_010_001);
        return input.Object;
    }

    private static IUserConverterService CreateUserConverter()
    {
        // The top-poster list resolves users through the converter; a loose mock returns null and the
        // service casts it, so hand back an empty list explicitly.
        var converter = new Mock<IUserConverterService>(MockBehavior.Loose);
        converter
            .Setup(x => x.GetUserListAsync(
                It.IsAny<IRequestInput>(), It.IsAny<List<long>>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<int>()))
            .ReturnsAsync([]);
        return converter.Object;
    }

    private static StatsService CreateService(IMetricsStore store, IMongoDatabase database) =>
        new(
            store,
            new GraphBuilder(new FakeAsyncGraphStore()),
            CreateUserConverter(),
            new Mock<IChatConverterService>(MockBehavior.Loose).Object,
            new Mock<IPublicForwardStore>(MockBehavior.Loose).Object,
            new Mock<IAsyncGraphStore>(MockBehavior.Loose).Object,
            new Mock<IMessageConverterService>(MockBehavior.Loose).Object,
            new Mock<IMessageAppService>(MockBehavior.Loose).Object,
            new Mock<IQueryProcessor>(MockBehavior.Loose).Object,
            database,
            StatsTestOptions.Create());

    private static int Today => (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / SecondsPerDay) * SecondsPerDay;

    private static async Task SeedReadAsync(IMongoDatabase database, long readerUserId, int date, long? targetPeerId = null)
    {
        await database.GetCollection<BsonDocument>("eventflow-readinghistoryreadmodel").InsertOneAsync(
            new BsonDocument
            {
                { "_id", $"readinghistory-{Guid.NewGuid():N}" },
                { "ReaderPeerId", readerUserId },
                { "TargetPeerId", targetPeerId ?? ChannelId },
                { "MessageId", 1L },
                { "Date", date },
            });
    }

    /// <summary>Records a post by <paramref name="posterUserId"/>, which feeds the top-poster breakdown.</summary>
    private static async Task SeedPostAsync(IMetricsStore store, long posterUserId, int utcDay)
    {
        var channel = new StatsEntityKey(StatsEntityType.Channel, ChannelId, 0);
        await store.RecordAsync(channel, StatsMetricNames.TopPosterMessages, utcDay, 1,
            new Dictionary<string, long> { [posterUserId.ToString()] = 1 });
        await store.RecordAsync(channel, StatsMetricNames.Messages, utcDay, 1);
    }

    [RequiresMongoDbFact]
    public async Task Posting_members_counts_the_distinct_users_who_posted()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new MetricsStore(mongo.Database);

        // Three distinct posters, one of whom posted twice.
        await SeedPostAsync(store, 2_010_001, Today);
        await SeedPostAsync(store, 2_010_001, Today);
        await SeedPostAsync(store, 2_010_002, Today);
        await SeedPostAsync(store, 2_010_003, Today);

        var stats = (TMegagroupStats)await CreateService(store, mongo.Database)
            .GetMegagroupStatsAsync(CreateInput(), ChannelId, dark: false);

        stats.Posters.ShouldBeOfType<TStatsAbsValueAndPrev>().Current.ShouldBe(3d);
    }

    [RequiresMongoDbFact]
    public async Task Viewing_members_counts_the_distinct_readers_of_this_supergroup()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new MetricsStore(mongo.Database);
        // A post so the reporting period covers today.
        await SeedPostAsync(store, 2_010_001, Today);

        // Four reads from two distinct users, plus a read of a different peer that must not be counted.
        await SeedReadAsync(mongo.Database, 2_020_001, Today + 10);
        await SeedReadAsync(mongo.Database, 2_020_001, Today + 20);
        await SeedReadAsync(mongo.Database, 2_020_002, Today + 30);
        await SeedReadAsync(mongo.Database, 2_020_009, Today + 40, targetPeerId: 800_000_009_999);

        var stats = (TMegagroupStats)await CreateService(store, mongo.Database)
            .GetMegagroupStatsAsync(CreateInput(), ChannelId, dark: false);

        stats.Viewers.ShouldBeOfType<TStatsAbsValueAndPrev>().Current.ShouldBe(2d);
    }

    [RequiresMongoDbFact]
    public async Task A_supergroup_with_no_reads_reports_zero_viewers_rather_than_failing()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new MetricsStore(mongo.Database);
        await SeedPostAsync(store, 2_010_001, Today);

        var stats = (TMegagroupStats)await CreateService(store, mongo.Database)
            .GetMegagroupStatsAsync(CreateInput(), ChannelId, dark: false);

        stats.Viewers.ShouldBeOfType<TStatsAbsValueAndPrev>().Current.ShouldBe(0d);
        // The client divides by `previous` only after a zero check, so 0/0 here is safe.
        stats.Viewers.ShouldBeOfType<TStatsAbsValueAndPrev>().Previous.ShouldBe(0d);
    }

    [RequiresMongoDbFact]
    public async Task A_recorded_posters_gauge_still_wins_over_the_derivation()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new MetricsStore(mongo.Database);
        var channel = new StatsEntityKey(StatsEntityType.Channel, ChannelId, 0);

        // Three distinct posters in the breakdown, but an explicit gauge says 42: the gauge is authoritative.
        await SeedPostAsync(store, 2_010_001, Today);
        await SeedPostAsync(store, 2_010_002, Today);
        await SeedPostAsync(store, 2_010_003, Today);
        await store.RecordAsync(channel, StatsMetricNames.Posters, Today, 42);

        var stats = (TMegagroupStats)await CreateService(store, mongo.Database)
            .GetMegagroupStatsAsync(CreateInput(), ChannelId, dark: false);

        stats.Posters.ShouldBeOfType<TStatsAbsValueAndPrev>().Current.ShouldBe(42d);
    }

    [RequiresMongoDbFact]
    public async Task Reads_outside_the_reporting_period_do_not_inflate_the_current_figure()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new MetricsStore(mongo.Database);
        await SeedPostAsync(store, 2_010_001, Today);

        // Inside the 7-day window.
        await SeedReadAsync(mongo.Database, 2_020_001, Today + 10);
        // Long before it — belongs to neither the period nor the previous period.
        await SeedReadAsync(mongo.Database, 2_020_055, Today - 400 * SecondsPerDay);

        var stats = (TMegagroupStats)await CreateService(store, mongo.Database)
            .GetMegagroupStatsAsync(CreateInput(), ChannelId, dark: false);

        stats.Viewers.ShouldBeOfType<TStatsAbsValueAndPrev>().Current.ShouldBe(1d);
    }
}
