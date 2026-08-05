using EventFlow.Queries;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Stats;
using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Regression tests for the "Notifications: NaN%" cell in the Android statistics overview.
///
/// <para>The client renders the figure as</para>
/// <code>
/// difPercent = (float) (stats.enabled_notifications.part / stats.enabled_notifications.total * 100f);
/// </code>
/// <para>with no zero guard — unlike every other overview percentage, which is written as
/// <c>previous == 0 ? 0 : ...</c>. Both operands are TL doubles, so <c>total == 0</c> makes this
/// <c>0.0 / 0.0</c> = <c>NaN</c>, formatted as the literal text "NaN%".</para>
///
/// <para>The <c>notify_on</c>/<c>muted</c> gauges are only written when something moves them (a join/leave
/// or a mute/unmute), so a channel that has seen neither since stats ingestion started has no recorded
/// pair at all and previously reported <c>total = 0</c>. The service now recomputes the pair from live
/// membership + the notify-settings read model in that case, mirroring <c>NotifyStateRecorder</c>.</para>
/// </summary>
public class EnabledNotificationsFallbackTests
{
    private const long ChannelId = 800_000_002_001;
    private const int NotifySettingsPeerType = (int)PeerType.Channel;

    private static IRequestInput CreateInput()
    {
        var input = new Mock<IRequestInput>(MockBehavior.Loose);
        input.SetupGet(x => x.UserId).Returns(2_010_001);
        return input.Object;
    }

    private static IQueryProcessor CreateQueryProcessor(int? participantsCount)
    {
        var queryProcessor = new Mock<IQueryProcessor>(MockBehavior.Loose);
        if (participantsCount.HasValue)
        {
            var channel = new Mock<IChannelReadModel>(MockBehavior.Loose);
            channel.SetupGet(x => x.ChannelId).Returns(ChannelId);
            channel.SetupGet(x => x.ParticipantsCount).Returns(participantsCount.Value);
            queryProcessor
                .Setup(x => x.ProcessAsync(It.IsAny<GetChannelByIdQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(channel.Object);
        }

        return queryProcessor.Object;
    }

    private static StatsService CreateService(IMetricsStore store, IMongoDatabase database, int? participantsCount) =>
        new(
            store,
            new GraphBuilder(new FakeAsyncGraphStore()),
            new Mock<IUserConverterService>(MockBehavior.Loose).Object,
            new Mock<IChatConverterService>(MockBehavior.Loose).Object,
            new Mock<IPublicForwardStore>(MockBehavior.Loose).Object,
            new Mock<IAsyncGraphStore>(MockBehavior.Loose).Object,
            new Mock<IMessageConverterService>(MockBehavior.Loose).Object,
            new Mock<IMessageAppService>(MockBehavior.Loose).Object,
            CreateQueryProcessor(participantsCount),
            database,
            StatsTestOptions.Create());

    /// <summary>Inserts a per-user notify-settings document for the channel, muted or not.</summary>
    private static async Task SeedNotifySettingsAsync(IMongoDatabase database, long userId, bool muted)
    {
        await database.GetCollection<BsonDocument>("eventflow-peernotifysettingsreadmodel").InsertOneAsync(
            new BsonDocument
            {
                { "_id", $"peernotifysettings-{ChannelId}-{userId}" },
                { "OwnerPeerId", userId },
                { "PeerId", ChannelId },
                { "PeerType", NotifySettingsPeerType },
                {
                    "NotifySettings", new BsonDocument
                    {
                        { "Silent", muted },
                        { "MuteUntil", 0 },
                        { "ShowPreviews", true },
                        { "Sound", "" },
                    }
                },
            });
    }

    /// <summary>
    /// The percentage the client computes: part / total * 100. NaN when total is 0, which is exactly the
    /// "NaN%" the overview cell printed.
    /// </summary>
    private static double ClientPercent(TStatsPercentValue value) => value.Part / value.Total * 100d;

    [RequiresMongoDbFact]
    public async Task A_channel_with_no_recorded_gauges_reports_a_usable_percentage_instead_of_NaN()
    {
        using var mongo = EmbeddedMongoServer.Start();
        // 5 participants, one of whom muted the channel — and no notify_on/muted gauge ever recorded.
        await SeedNotifySettingsAsync(mongo.Database, 2_010_001, muted: true);
        await SeedNotifySettingsAsync(mongo.Database, 2_010_002, muted: false);

        var service = CreateService(new MetricsStore(mongo.Database), mongo.Database, participantsCount: 5);

        var stats = await service.GetBroadcastStatsAsync(CreateInput(), ChannelId, dark: false);

        var percent = stats.EnabledNotifications.ShouldBeOfType<TStatsPercentValue>();
        percent.Total.ShouldBe(5d);
        // 5 participants - 1 muted.
        percent.Part.ShouldBe(4d);
        ClientPercent(percent).ShouldBe(80d);
        double.IsNaN(ClientPercent(percent)).ShouldBeFalse();
    }

    [RequiresMongoDbFact]
    public async Task A_channel_with_no_muted_members_reports_a_hundred_percent()
    {
        using var mongo = EmbeddedMongoServer.Start();

        var service = CreateService(new MetricsStore(mongo.Database), mongo.Database, participantsCount: 13);

        var stats = await service.GetBroadcastStatsAsync(CreateInput(), ChannelId, dark: false);

        var percent = stats.EnabledNotifications.ShouldBeOfType<TStatsPercentValue>();
        percent.Part.ShouldBe(13d);
        percent.Total.ShouldBe(13d);
        ClientPercent(percent).ShouldBe(100d);
    }

    [RequiresMongoDbFact]
    public async Task Muted_documents_from_users_who_left_cannot_push_the_percentage_negative()
    {
        using var mongo = EmbeddedMongoServer.Start();
        // Notify settings survive leaving the channel, so there can be more muted documents than members.
        for (var i = 0; i < 4; i++)
        {
            await SeedNotifySettingsAsync(mongo.Database, 2_010_001 + i, muted: true);
        }

        var service = CreateService(new MetricsStore(mongo.Database), mongo.Database, participantsCount: 2);

        var stats = await service.GetBroadcastStatsAsync(CreateInput(), ChannelId, dark: false);

        var percent = stats.EnabledNotifications.ShouldBeOfType<TStatsPercentValue>();
        // muted is clamped to the participant count, so part never goes below zero.
        percent.Part.ShouldBe(0d);
        percent.Total.ShouldBe(2d);
        ClientPercent(percent).ShouldBe(0d);
    }

    [RequiresMongoDbFact]
    public async Task An_empty_channel_still_reports_zeroes_rather_than_inventing_members()
    {
        using var mongo = EmbeddedMongoServer.Start();

        var service = CreateService(new MetricsStore(mongo.Database), mongo.Database, participantsCount: 0);

        var stats = await service.GetBroadcastStatsAsync(CreateInput(), ChannelId, dark: false);

        // Nothing to report: the official server sends {0,0} for an empty channel too. The client still
        // divides 0 by 0 here, but a channel with no members has no statistics screen to speak of.
        var percent = stats.EnabledNotifications.ShouldBeOfType<TStatsPercentValue>();
        percent.Part.ShouldBe(0d);
        percent.Total.ShouldBe(0d);
    }

    [RequiresMongoDbFact]
    public async Task An_unknown_channel_reports_zeroes_rather_than_throwing()
    {
        using var mongo = EmbeddedMongoServer.Start();

        // No channel read model at all: the query processor returns null.
        var service = CreateService(new MetricsStore(mongo.Database), mongo.Database, participantsCount: null);

        var stats = await service.GetBroadcastStatsAsync(CreateInput(), ChannelId, dark: false);

        var percent = stats.EnabledNotifications.ShouldBeOfType<TStatsPercentValue>();
        percent.Part.ShouldBe(0d);
        percent.Total.ShouldBe(0d);
    }

    [RequiresMongoDbFact]
    public async Task Recorded_gauges_still_win_over_the_live_fallback()
    {
        using var mongo = EmbeddedMongoServer.Start();
        // The read model says everyone muted the channel, but the recorded gauges say otherwise: the
        // recorded pair is authoritative and the fallback must not run.
        for (var i = 0; i < 9; i++)
        {
            await SeedNotifySettingsAsync(mongo.Database, 2_010_001 + i, muted: true);
        }

        var store = new MetricsStore(mongo.Database);
        var channel = new StatsEntityKey(StatsEntityType.Channel, ChannelId, 0);
        var utcDay = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 86_400) * 86_400;
        await store.RecordAsync(channel, StatsMetricNames.NotifyOn, utcDay, 7);
        await store.RecordAsync(channel, StatsMetricNames.Muted, utcDay, 3);

        var service = CreateService(store, mongo.Database, participantsCount: 9);

        var stats = await service.GetBroadcastStatsAsync(CreateInput(), ChannelId, dark: false);

        var percent = stats.EnabledNotifications.ShouldBeOfType<TStatsPercentValue>();
        percent.Part.ShouldBe(7d);
        percent.Total.ShouldBe(10d);
    }
}
