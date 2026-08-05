using EventFlow.Queries;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Handlers.LatestLayer.Payments;
using MyTelegram.Messenger.Services.Stats;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Regression tests for the "own wallet" branch of the Stars revenue API.
///
/// <para>The Android client's Stars balance screen ("Stars" button under the balance) opens
/// <c>BotStarsActivity(TYPE_STARS, clientUserId)</c> and calls
/// <c>payments.getStarsRevenueStats</c> / <c>getStarsStatus</c> / <c>getStarsTransactions</c>
/// for the user's <em>own</em> peer — it has a dedicated self mode for exactly this
/// (<c>self == botId == clientUserId</c>, strings <c>SelfStarsOverviewInfo</c> /
/// <c>SelfStarsWithdrawInfo</c>). The peer arrives either as <c>inputPeerSelf</c> or as an explicit
/// <c>inputPeerUser</c> carrying the caller's own id, depending on which screen issues the request.</para>
///
/// <para>Previously the handlers treated any user peer as "must be a bot I own" and answered
/// <c>PEER_ID_INVALID</c>, so the screen stayed without a <c>status</c>. These tests pin down that
/// both self forms now return a real <c>starsRevenueStats</c> with a populated status and a full
/// 30-point revenue graph, and that a non-owned bot peer still fails with <c>PEER_ID_INVALID</c>.</para>
/// </summary>
public class StarsRevenueSelfWalletTests
{
    private const long CallerUserId = 2_010_001;
    private const long ForeignBotId = 600_000_000_000;

    private static IRequestInput CreateInput()
    {
        var input = new Mock<IRequestInput>(MockBehavior.Loose);
        input.SetupGet(x => x.UserId).Returns(CallerUserId);
        return input.Object;
    }

    private static IOptionsMonitor<MyTelegramMessengerServerOptions> CreateOptions()
    {
        var monitor = new Mock<IOptionsMonitor<MyTelegramMessengerServerOptions>>(MockBehavior.Loose);
        monitor.SetupGet(x => x.CurrentValue).Returns(new MyTelegramMessengerServerOptions());
        return monitor.Object;
    }

    private static GetStarsRevenueStatsHandler CreateHandler(
        IMongoDatabase database,
        IQueryProcessor? queryProcessor = null)
    {
        var queries = queryProcessor ?? new Mock<IQueryProcessor>(MockBehavior.Loose).Object;
        return new GetStarsRevenueStatsHandler(
            database,
            new PeerHelper(),
            queries,
            new GraphBuilder(new FakeAsyncGraphStore()),
            CreateOptions());
    }

    /// <summary>
    /// <see cref="RpcResultObjectHandler{TInput,TOutput}.HandleAsync"/> wraps the payload in a
    /// <c>rpc_result</c>; unwrap it so the assertions read the actual TL object.
    /// </summary>
    private static IObject Unwrap(IObject result) =>
        result is TRpcResult rpcResult ? rpcResult.Result : result;

    private static async Task SeedStarsBalanceAsync(IMongoDatabase database, long userId, long balance)
    {
        // StarsBalanceDocument.Id is an ObjectId, so let the driver generate it.
        await database.GetCollection<MongoDB.Bson.BsonDocument>("star-balances").InsertOneAsync(
            new MongoDB.Bson.BsonDocument
            {
                { "_id", MongoDB.Bson.ObjectId.GenerateNewId() },
                { "UserId", userId },
                { "Balance", balance },
            });
    }

    private static async Task SeedStarsTransactionAsync(IMongoDatabase database, long userId, long amount, int date)
    {
        await database.GetCollection<MongoDB.Bson.BsonDocument>("star-transactions").InsertOneAsync(
            new MongoDB.Bson.BsonDocument
            {
                { "_id", MongoDB.Bson.ObjectId.GenerateNewId() },
                { "UserId", userId },
                { "Amount", amount },
                { "Date", date },
            });
    }

    [RequiresMongoDbFact]
    public async Task GetStarsRevenueStats_for_inputPeerSelf_returns_the_callers_own_wallet()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedStarsBalanceAsync(mongo.Database, CallerUserId, 2_500);
        var nowUnix = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await SeedStarsTransactionAsync(mongo.Database, CallerUserId, 1_500, nowUnix - 3_600);

        var handler = CreateHandler(mongo.Database);
        var result = await handler.HandleAsync(CreateInput(), new Schema.Payments.RequestGetStarsRevenueStats
        {
            Peer = new TInputPeerSelf(),
        });

        var stats = Unwrap(result).ShouldBeOfType<Schema.Payments.TStarsRevenueStats>();
        stats.Status.ShouldNotBeNull();
        var status = stats.Status.ShouldBeOfType<TStarsRevenueStatus>();
        status.CurrentBalance.ShouldBeOfType<TStarsAmount>().Amount.ShouldBe(2_500);
        status.AvailableBalance.ShouldBeOfType<TStarsAmount>().Amount.ShouldBe(2_500);
        // Overall revenue sums the caller's positive transactions.
        status.OverallRevenue.ShouldBeOfType<TStarsAmount>().Amount.ShouldBe(1_500);
        // 2500 >= the 1000-star withdrawal minimum.
        status.WithdrawalEnabled.ShouldBeTrue();
        stats.RevenueGraph.ShouldNotBeNull();
    }

    [RequiresMongoDbFact]
    public async Task GetStarsRevenueStats_for_an_explicit_self_user_peer_returns_the_own_wallet_too()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedStarsBalanceAsync(mongo.Database, CallerUserId, 400);

        var handler = CreateHandler(mongo.Database);
        var result = await handler.HandleAsync(CreateInput(), new Schema.Payments.RequestGetStarsRevenueStats
        {
            // The client sends the caller's own id as an explicit peer on some screens.
            Peer = new TInputPeerUser { UserId = CallerUserId, AccessHash = 0 },
        });

        var stats = Unwrap(result).ShouldBeOfType<Schema.Payments.TStarsRevenueStats>();
        var status = stats.Status.ShouldBeOfType<TStarsRevenueStatus>();
        status.CurrentBalance.ShouldBeOfType<TStarsAmount>().Amount.ShouldBe(400);
        // Below the 1000-star minimum, so withdrawal stays disabled.
        status.WithdrawalEnabled.ShouldBeFalse();
    }

    [RequiresMongoDbFact]
    public async Task GetStarsRevenueStats_for_ton_returns_a_starsTonAmount_status()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await mongo.Database.GetCollection<MongoDB.Bson.BsonDocument>("ton-balances").InsertOneAsync(
            new MongoDB.Bson.BsonDocument
            {
                { "_id", MongoDB.Bson.ObjectId.GenerateNewId() },
                { "UserId", CallerUserId },
                { "Balance", 7_000_000_000L },
            });

        var handler = CreateHandler(mongo.Database);
        var result = await handler.HandleAsync(CreateInput(), new Schema.Payments.RequestGetStarsRevenueStats
        {
            Peer = new TInputPeerSelf(),
            Ton = true,
        });

        var stats = Unwrap(result).ShouldBeOfType<Schema.Payments.TStarsRevenueStats>();
        var status = stats.Status.ShouldBeOfType<TStarsRevenueStatus>();
        // TON amounts must use starsTonAmount (nanotons), never starsAmount.
        status.CurrentBalance.ShouldBeOfType<TStarsTonAmount>().Amount.ShouldBe(7_000_000_000L);
        // TON has no client-side minimum, so any positive balance is withdrawable.
        status.WithdrawalEnabled.ShouldBeTrue();
    }

    [RequiresMongoDbFact]
    public async Task GetStarsRevenueStats_for_a_bot_the_caller_does_not_own_still_throws_PEER_ID_INVALID()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var queryProcessor = new Mock<IQueryProcessor>(MockBehavior.Loose);

        var handler = CreateHandler(mongo.Database, queryProcessor.Object);
        var exception = await Should.ThrowAsync<Exception>(async () =>
            await handler.HandleAsync(CreateInput(), new Schema.Payments.RequestGetStarsRevenueStats
            {
                Peer = new TInputPeerUser { UserId = ForeignBotId, AccessHash = 0 },
            }));

        exception.Message.ShouldContain("PEER_ID_INVALID");
    }
}
