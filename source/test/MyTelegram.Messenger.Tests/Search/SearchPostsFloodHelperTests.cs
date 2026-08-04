using MyTelegram.Messenger.Handlers.LatestLayer.Channels;
using MyTelegram.Messenger.Tests.Stats;

namespace MyTelegram.Messenger.Tests.Search;

/// <summary>
/// Feature: the daily quota behind the <a href="https://corefork.telegram.org/api/search#posts-tab">posts tab</a>
/// of global search.
///
/// <para>
/// A number of global post searches are free per UTC day; afterwards each search costs Stars. The
/// counter has to be atomic (concurrent searches must not over-grant free quota) and must not drift
/// upwards once the quota is spent, otherwise a user who keeps searching would never see the counter
/// settle. These tests run against a real <c>mongod</c> because that behaviour lives in the
/// find-and-modify semantics rather than in the C# above it.
/// </para>
/// </summary>
public class SearchPostsFloodHelperTests
{
    private const long UserId = 4242;

    [RequiresMongoDbFact]
    public async Task A_fresh_user_has_the_whole_daily_quota_and_searches_for_free()
    {
        using var mongo = EmbeddedMongoServer.Start();

        var state = await SearchPostsFloodHelper.GetStateAsync(mongo.Database, UserId);

        state.Remains.ShouldBe(SearchPostsFloodHelper.TotalDaily);
        state.QueryIsFree.ShouldBeTrue();
        // wait_till is only meaningful once the quota is exhausted.
        state.WaitTill.ShouldBe(0);
    }

    [RequiresMongoDbFact]
    public async Task Each_free_search_decrements_the_remaining_quota()
    {
        using var mongo = EmbeddedMongoServer.Start();

        (await SearchPostsFloodHelper.TryConsumeFreeSearchAsync(mongo.Database, UserId)).ShouldBeTrue();
        (await SearchPostsFloodHelper.GetStateAsync(mongo.Database, UserId)).Remains
            .ShouldBe(SearchPostsFloodHelper.TotalDaily - 1);

        (await SearchPostsFloodHelper.TryConsumeFreeSearchAsync(mongo.Database, UserId)).ShouldBeTrue();
        (await SearchPostsFloodHelper.GetStateAsync(mongo.Database, UserId)).Remains
            .ShouldBe(SearchPostsFloodHelper.TotalDaily - 2);
    }

    [RequiresMongoDbFact]
    public async Task The_quota_is_tracked_per_user()
    {
        using var mongo = EmbeddedMongoServer.Start();

        await SearchPostsFloodHelper.TryConsumeFreeSearchAsync(mongo.Database, UserId);

        (await SearchPostsFloodHelper.GetStateAsync(mongo.Database, UserId + 1)).Remains
            .ShouldBe(SearchPostsFloodHelper.TotalDaily);
    }

    [RequiresMongoDbFact]
    public async Task Once_the_quota_is_spent_searches_stop_being_free_and_report_a_reset_time()
    {
        using var mongo = EmbeddedMongoServer.Start();

        for (var i = 0; i < SearchPostsFloodHelper.TotalDaily; i++)
        {
            (await SearchPostsFloodHelper.TryConsumeFreeSearchAsync(mongo.Database, UserId)).ShouldBeTrue();
        }

        (await SearchPostsFloodHelper.TryConsumeFreeSearchAsync(mongo.Database, UserId)).ShouldBeFalse();

        var state = await SearchPostsFloodHelper.GetStateAsync(mongo.Database, UserId);
        state.Remains.ShouldBe(0);
        state.QueryIsFree.ShouldBeFalse();
        // The client needs to know when free searches come back.
        state.WaitTill.ShouldBeGreaterThan((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    [RequiresMongoDbFact]
    public async Task Paid_searches_do_not_push_the_counter_past_the_daily_total()
    {
        using var mongo = EmbeddedMongoServer.Start();

        for (var i = 0; i < SearchPostsFloodHelper.TotalDaily; i++)
        {
            await SearchPostsFloodHelper.TryConsumeFreeSearchAsync(mongo.Database, UserId);
        }

        // Every one of these is a paid search: the stored counter must stay pinned at the total
        // instead of drifting up, so remains stays 0 rather than going negative.
        for (var i = 0; i < 5; i++)
        {
            (await SearchPostsFloodHelper.TryConsumeFreeSearchAsync(mongo.Database, UserId)).ShouldBeFalse();
        }

        (await SearchPostsFloodHelper.GetStateAsync(mongo.Database, UserId)).Remains.ShouldBe(0);
    }

    [RequiresMongoDbFact]
    public async Task Concurrent_searches_never_grant_more_than_the_daily_quota()
    {
        using var mongo = EmbeddedMongoServer.Start();

        var attempts = SearchPostsFloodHelper.TotalDaily + 40;
        var results = await Task.WhenAll(Enumerable.Range(0, attempts)
            .Select(_ => SearchPostsFloodHelper.TryConsumeFreeSearchAsync(mongo.Database, UserId)));

        results.Count(p => p).ShouldBe(SearchPostsFloodHelper.TotalDaily);
    }
}
