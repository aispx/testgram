using EventFlow.Queries;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.EventFlow.MongoDB;
using MyTelegram.EventFlow.MongoDB.ReadStores;
using MyTelegram.EventFlow.ReadStores;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Queries;
using MyTelegram.QueryHandlers.MongoDB.Updates;
using MyTelegram.ReadModel.Interfaces;
using PersistedUpdatesReadModel = MyTelegram.ReadModel.MongoDB.UpdatesReadModel;

namespace MyTelegram.Messenger.Tests.Updates;

/// <summary>
/// Regression tests for the pts lower bound in <see cref="GetUpdatesQueryHandler"/>.
///
/// <para>The filter used to be applied conditionally — <c>WhereIf(query.MinPts &gt; 0, ...)</c> — so a
/// client sending <c>pts = 0</c> skipped it entirely and got the whole update box back, truncated to a
/// full page. <c>DifferenceConverterService</c> reads a full page as "there is more" and answers with
/// <c>differenceSlice</c>, but the server-side cursor only advances once the client acks. The next
/// request arrived with <c>pts = 0</c> again and received the same page, so the client polled
/// <c>updates.getDifference</c> forever (measured at ~4 calls/second against a box with nothing new).
/// </para>
///
/// <para>These run against <see cref="EmbeddedMongoServer"/> through the production Mongo read-model
/// store, so the predicate is translated by the same code path that serves a live getDifference.</para>
/// </summary>
public class GetUpdatesQueryPtsBoundTests
{
    private const long OwnerPeerId = 2_010_001;
    private const long SelfUserId = OwnerPeerId;

    [RequiresMongoDbFact]
    public async Task A_zero_pts_does_not_return_the_whole_box()
    {
        using var server = EmbeddedMongoServer.Start()!;
        var handler = await SeedAsync(server, ptsValues: [0, 1, 2, 3]);

        var rows = await handler.ExecuteQueryAsync(
            new GetUpdatesQuery(SelfUserId, OwnerPeerId, MinPts: 0, Date: 0, Limit: 500),
            CancellationToken.None);

        // Only rows strictly above pts 0 — the pts-0 row sits outside the pts box and replays via
        // GlobalSeqNo instead.
        rows.Select(p => p.Pts).OrderBy(p => p).ShouldBe([1, 2, 3]);
    }

    [RequiresMongoDbFact]
    public async Task A_caught_up_client_receives_nothing()
    {
        using var server = EmbeddedMongoServer.Start()!;
        var handler = await SeedAsync(server, ptsValues: [0, 1, 2, 3]);

        var rows = await handler.ExecuteQueryAsync(
            new GetUpdatesQuery(SelfUserId, OwnerPeerId, MinPts: 3, Date: 0, Limit: 500),
            CancellationToken.None);

        rows.ShouldBeEmpty();
    }

    [RequiresMongoDbFact]
    public async Task A_partially_behind_client_receives_only_what_it_is_missing()
    {
        using var server = EmbeddedMongoServer.Start()!;
        var handler = await SeedAsync(server, ptsValues: [1, 2, 3, 4, 5]);

        var rows = await handler.ExecuteQueryAsync(
            new GetUpdatesQuery(SelfUserId, OwnerPeerId, MinPts: 3, Date: 0, Limit: 500),
            CancellationToken.None);

        rows.Select(p => p.Pts).OrderBy(p => p).ShouldBe([4, 5]);
    }

    [RequiresMongoDbFact]
    public async Task A_zero_pts_no_longer_fills_a_whole_page_and_so_no_longer_forces_a_slice()
    {
        using var server = EmbeddedMongoServer.Start()!;
        // A box far larger than the page limit, with every row already below the client's pts.
        var handler = await SeedAsync(server, ptsValues: [.. Enumerable.Range(1, 40)]);

        var rows = await handler.ExecuteQueryAsync(
            new GetUpdatesQuery(SelfUserId, OwnerPeerId, MinPts: 40, Date: 0, Limit: 10),
            CancellationToken.None);

        // The old conditional filter returned a full page here whenever the client sent 0, which the
        // converter turned into an endless slice. A caught-up client must come back empty.
        rows.Count.ShouldBeLessThan(10);
        rows.ShouldBeEmpty();
    }

    private static async Task<GetUpdatesQueryHandler> SeedAsync(EmbeddedMongoServer server, int[] ptsValues)
    {
        var collection = server.Database.GetCollection<PersistedUpdatesReadModel>("eventflow-updatesreadmodel");
        var globalSeqNo = 1L;
        foreach (var pts in ptsValues)
        {
            await collection.InsertOneAsync(Row(globalSeqNo++, pts));
        }

        return new GetUpdatesQueryHandler(RealStore(server.Database));
    }

    private static IQueryOnlyReadModelStore<PersistedUpdatesReadModel> RealStore(IMongoDatabase database)
    {
        return new MongoDbQueryOnlyReadModelStore<PersistedUpdatesReadModel>(
            new QueryOnlyReadModelDescriptionProvider(),
            new MongoDbContext(database),
            NullLogger<MongoDbQueryOnlyReadModelStore<PersistedUpdatesReadModel, IMongoDbContext>>.Instance);
    }

    /// <summary>
    /// A row in the shape <c>UpdatesReadModel.ApplyAsync</c> writes. Built as BSON and deserialised by
    /// the driver because every property has a private setter, which also pins these field names to the
    /// real persisted schema.
    /// </summary>
    private static PersistedUpdatesReadModel Row(long globalSeqNo, int pts)
    {
        var document = new BsonDocument
        {
            ["_id"] = $"{OwnerPeerId}-{globalSeqNo}-{Guid.NewGuid():N}",
            ["OwnerPeerId"] = OwnerPeerId,
            ["ChannelId"] = 0L,
            ["ExcludeAuthKeyId"] = BsonNull.Value,
            ["ExcludeUserId"] = BsonNull.Value,
            ["OnlySendToUserId"] = BsonNull.Value,
            ["OnlySendToThisAuthKeyId"] = BsonNull.Value,
            ["UpdatesType"] = (int)UpdatesType.Updates,
            ["MessageId"] = BsonNull.Value,
            ["Pts"] = pts,
            ["Date"] = 1_700_000_000,
            ["GlobalSeqNo"] = globalSeqNo,
            ["Updates"] = BsonNull.Value,
            ["Users"] = BsonNull.Value,
            ["Chats"] = BsonNull.Value
        };

        return MongoDB.Bson.Serialization.BsonSerializer.Deserialize<PersistedUpdatesReadModel>(document);
    }
}
