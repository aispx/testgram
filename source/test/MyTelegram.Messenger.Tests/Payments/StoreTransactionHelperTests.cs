using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger.Handlers.LatestLayer.Payments;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.Payments;

/// <summary>
/// Feature: crediting Stars for an app store purchase.
///
/// <para>
/// This server can check neither an App Store nor a Play receipt, so the amount in an unmatched
/// receipt is whatever the caller claims. Accepting it unconditionally hands every account an
/// unlimited Stars tap, so the path is refused unless the stand opts in, and even then a receipt pays
/// out once and an account has a lifetime ceiling.
/// See https://corefork.telegram.org/method/payments.assignPlayMarketTransaction
/// </para>
/// </summary>
public class StoreTransactionHelperTests
{
    private const long UserId = 2010001;
    private const long OtherUserId = 2010002;

    [RequiresMongoDbFact]
    public async Task An_unverifiable_receipt_buys_nothing_by_default()
    {
        using var mongo = EmbeddedMongoServer.Start();

        var exception = await Should.ThrowAsync<RpcException>(() => CreditAsync(mongo.Database, Disabled(), UserId, 1000));

        exception.RpcError.Message.ShouldBe("PAYMENT_PROVIDER_INVALID");
        (await StarsBalanceHelper.GetBalanceAsync(mongo.Database, UserId)).ShouldBe(0);
    }

    [RequiresMongoDbFact]
    public async Task Opting_in_credits_the_amount_the_purpose_asked_for()
    {
        using var mongo = EmbeddedMongoServer.Start();

        await CreditAsync(mongo.Database, Enabled(), UserId, 1000);

        (await StarsBalanceHelper.GetBalanceAsync(mongo.Database, UserId)).ShouldBe(1000);
    }

    [RequiresMongoDbFact]
    public async Task The_same_receipt_never_pays_out_twice()
    {
        // Without this the method is a loop: call it N times, hold N times the Stars.
        using var mongo = EmbeddedMongoServer.Start();

        await CreditAsync(mongo.Database, Enabled(), UserId, 1000, receipt: "receipt-1");

        await Should.ThrowAsync<RpcException>(() =>
            CreditAsync(mongo.Database, Enabled(), UserId, 1000, receipt: "receipt-1"));

        (await StarsBalanceHelper.GetBalanceAsync(mongo.Database, UserId)).ShouldBe(1000);
    }

    [RequiresMongoDbFact]
    public async Task A_receipt_someone_else_already_spent_is_refused()
    {
        // One receipt is one purchase; it does not become a second one in another account's hands.
        using var mongo = EmbeddedMongoServer.Start();

        await CreditAsync(mongo.Database, Enabled(), UserId, 1000, receipt: "receipt-1");

        await Should.ThrowAsync<RpcException>(() =>
            CreditAsync(mongo.Database, Enabled(), OtherUserId, 1000, receipt: "receipt-1"));

        (await StarsBalanceHelper.GetBalanceAsync(mongo.Database, OtherUserId)).ShouldBe(0);
    }

    [RequiresMongoDbFact]
    public async Task Receipts_are_told_apart_by_platform()
    {
        using var mongo = EmbeddedMongoServer.Start();

        await CreditAsync(mongo.Database, Enabled(), UserId, 1000, receipt: "receipt-1", platform: "appstore");
        await CreditAsync(mongo.Database, Enabled(), UserId, 1000, receipt: "receipt-1", platform: "playmarket");

        (await StarsBalanceHelper.GetBalanceAsync(mongo.Database, UserId)).ShouldBe(2000);
    }

    [RequiresMongoDbFact]
    public async Task The_ceiling_holds_across_separate_receipts()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var config = Enabled(limit: 2500);

        await CreditAsync(mongo.Database, config, UserId, 1000, receipt: "receipt-1");
        await CreditAsync(mongo.Database, config, UserId, 1000, receipt: "receipt-2");

        await Should.ThrowAsync<RpcException>(() =>
            CreditAsync(mongo.Database, config, UserId, 1000, receipt: "receipt-3"));

        (await StarsBalanceHelper.GetBalanceAsync(mongo.Database, UserId)).ShouldBe(2000);
    }

    [RequiresMongoDbFact]
    public async Task A_rejected_top_up_gives_its_reservation_back()
    {
        // The refused amount must not sit in the running total and eat the account's remaining room.
        using var mongo = EmbeddedMongoServer.Start();
        var config = Enabled(limit: 2000);

        await Should.ThrowAsync<RpcException>(() =>
            CreditAsync(mongo.Database, config, UserId, 5000, receipt: "receipt-1"));

        await CreditAsync(mongo.Database, config, UserId, 2000, receipt: "receipt-2");

        (await StarsBalanceHelper.GetBalanceAsync(mongo.Database, UserId)).ShouldBe(2000);
    }

    [RequiresMongoDbFact]
    public async Task A_replayed_receipt_gives_its_reservation_back()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var config = Enabled(limit: 2000);

        await CreditAsync(mongo.Database, config, UserId, 1000, receipt: "receipt-1");
        await Should.ThrowAsync<RpcException>(() =>
            CreditAsync(mongo.Database, config, UserId, 1000, receipt: "receipt-1"));

        // The replay reserved 1000 against the ceiling and has to hand it back, or this fails.
        await CreditAsync(mongo.Database, config, UserId, 1000, receipt: "receipt-2");

        (await StarsBalanceHelper.GetBalanceAsync(mongo.Database, UserId)).ShouldBe(2000);
    }

    [RequiresMongoDbFact]
    public async Task A_purpose_that_asks_for_no_stars_is_invalid()
    {
        using var mongo = EmbeddedMongoServer.Start();

        foreach (var stars in new long[] { 0, -1000 })
        {
            var exception = await Should.ThrowAsync<RpcException>(() =>
                CreditAsync(mongo.Database, Enabled(), UserId, stars, receipt: $"receipt-{stars}"));

            exception.RpcError.Message.ShouldBe("INPUT_PURPOSE_INVALID");
        }

        (await StarsBalanceHelper.GetBalanceAsync(mongo.Database, UserId)).ShouldBe(0);
    }

    [RequiresMongoDbFact]
    public async Task Concurrent_top_ups_cannot_push_the_account_over_the_ceiling()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var config = Enabled(limit: 1000);

        var attempts = Enumerable.Range(0, 8)
            .Select(i => Record(() => CreditAsync(mongo.Database, config, UserId, 1000, receipt: $"receipt-{i}")));

        await Task.WhenAll(attempts);

        (await StarsBalanceHelper.GetBalanceAsync(mongo.Database, UserId)).ShouldBeLessThanOrEqualTo(1000);
    }

    private static async Task Record(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (RpcException)
        {
            // Losing the race is the expected outcome for all but one caller.
        }
    }

    private static Task CreditAsync(
        IMongoDatabase database,
        PaymentsConfig config,
        long userId,
        long stars,
        string receipt = "receipt",
        string platform = "playmarket")
    {
        var sender = new Mock<IObjectMessageSender>(MockBehavior.Loose);

        return StoreTransactionHelper.CreditUnverifiedTopupAsync(
            database, sender.Object, config, platform, receipt, userId, stars, $"Top-up: {stars} stars");
    }

    private static PaymentsConfig Disabled() => new();

    private static PaymentsConfig Enabled(long limit = 10_000) => new()
    {
        AllowUnverifiedTopup = true,
        UnverifiedTopupLimit = limit
    };
}
