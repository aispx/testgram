using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Messenger.Tests.Stats;

namespace MyTelegram.Messenger.Tests.Stories;

/// <summary>
/// Feature: stories — profile listing pagination.
///
/// <para>
/// A client that gets <c>stories.stories.count</c> larger than the number of stories it can hold
/// decides the page was short and asks for the next one. Pagination advances by <c>StoryId</c>, so
/// when the surplus comes from duplicate ids the follow-up request returns the same page and the
/// client loops — the observed ~8 req/s flood of <c>stories.getPinnedStories</c> that left the
/// profile stuck loading. These tests pin the two ways the count could overshoot: duplicate
/// <c>StoryId</c> documents, and stories the privacy filter removes for the viewer.
/// </para>
/// </summary>
public class StoryPagingHelperTests
{
    private const long OwnerId = 2010001;

    private static StoryDocument NewStory(int storyId, bool pinnedToTop = false) => new()
    {
        Id = ObjectId.GenerateNewId(),
        OwnerPeerId = OwnerId,
        OwnerPeerType = StoryHelper.PeerTypeUser,
        StoryId = storyId,
        PinnedToTop = pinnedToTop,
        Pinned = true,
        Deleted = false
    };

    private static FilterDefinition<StoryDocument> PinnedFilter =>
        Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, OwnerId)
        & Builders<StoryDocument>.Filter.Eq(s => s.Pinned, true)
        & Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false);

    [RequiresMongoDbFact]
    public async Task Duplicate_documents_of_one_story_are_counted_once()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = mongo.Database.GetCollection<StoryDocument>("stories");
        // The live shape: StoryId 11001 duplicated 4x, 11002 duplicated 6x, plus one clean story.
        await collection.InsertManyAsync(
        [
            NewStory(11001), NewStory(11001), NewStory(11001), NewStory(11001),
            NewStory(11002), NewStory(11002), NewStory(11002),
            NewStory(11002), NewStory(11002), NewStory(11002),
            NewStory(12001)
        ]);

        var count = await StoryPagingHelper.CountDistinctStoriesAsync(collection, PinnedFilter);

        // 11 documents, 3 stories. Counting documents is what produced the loop.
        count.ShouldBe(3);
    }

    [Fact]
    public void Deduplicating_a_page_keeps_one_copy_of_each_story()
    {
        var page = new[]
        {
            NewStory(11002), NewStory(11002), NewStory(11002),
            NewStory(11001), NewStory(11001)
        };

        var result = StoryPagingHelper.DeduplicatePage(page, limit: 100);

        result.Select(s => s.StoryId).ShouldBe([11002, 11001]);
    }

    [Fact]
    public void Deduplicating_preserves_the_incoming_sort_order()
    {
        // Pinned-to-top first, then newest id first — the order the query established and the order
        // the profile renders in.
        var page = new[]
        {
            NewStory(11001, pinnedToTop: true), NewStory(11001, pinnedToTop: true),
            NewStory(13001), NewStory(12001), NewStory(12001)
        };

        var result = StoryPagingHelper.DeduplicatePage(page, limit: 100);

        result.Select(s => s.StoryId).ShouldBe([11001, 13001, 12001]);
        result[0].PinnedToTop.ShouldBeTrue();
    }

    [Fact]
    public void Deduplicating_trims_the_page_to_the_requested_limit()
    {
        var page = new[] { NewStory(5), NewStory(4), NewStory(3), NewStory(2), NewStory(1) };

        var result = StoryPagingHelper.DeduplicatePage(page, limit: 3);

        result.Count.ShouldBe(3);
        result.Select(s => s.StoryId).ShouldBe([5, 4, 3]);
    }

    [Fact]
    public void A_full_page_reports_the_total_so_the_client_keeps_paging()
    {
        // Page is full → more stories exist → the client must be told to continue.
        var count = StoryPagingHelper.ResolveCount(
            distinctTotal: 40, deliveredCount: 10, fetchedCount: 10, limit: 10, isFirstPage: true);

        count.ShouldBe(40);
    }

    [Fact]
    public void A_short_first_page_reports_only_what_was_delivered()
    {
        // Privacy filter removed 4 of 10 stories. Reporting 10 would make the client ask for a
        // second page that cannot exist, and the request would repeat indefinitely.
        var count = StoryPagingHelper.ResolveCount(
            distinctTotal: 10, deliveredCount: 6, fetchedCount: 6, limit: 100, isFirstPage: true);

        count.ShouldBe(6);
    }

    [Fact]
    public void A_short_first_page_with_nothing_visible_reports_zero()
    {
        var count = StoryPagingHelper.ResolveCount(
            distinctTotal: 10, deliveredCount: 0, fetchedCount: 3, limit: 100, isFirstPage: true);

        count.ShouldBe(0);
    }

    [Fact]
    public void A_short_later_page_does_not_shrink_below_the_total()
    {
        // Later pages must not report less than the client already holds from earlier pages,
        // so the total acts as a floor.
        var count = StoryPagingHelper.ResolveCount(
            distinctTotal: 40, deliveredCount: 5, fetchedCount: 5, limit: 10, isFirstPage: false);

        count.ShouldBe(40);
    }

    [Fact]
    public void The_reported_count_never_exceeds_what_a_short_first_page_delivered()
    {
        // The loop condition, stated directly: on a terminal first page the count may not promise
        // more than the client received.
        foreach (var delivered in Enumerable.Range(0, 20))
        {
            var count = StoryPagingHelper.ResolveCount(
                distinctTotal: 999, deliveredCount: delivered, fetchedCount: delivered,
                limit: 100, isFirstPage: true);

            count.ShouldBeLessThanOrEqualTo(delivered);
        }
    }

    [RequiresMongoDbFact]
    public async Task The_live_duplicate_shape_no_longer_over_reports()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = mongo.Database.GetCollection<StoryDocument>("stories");
        // Exactly what the server holds: 18 pinned documents covering 10 distinct stories.
        var docs = new List<StoryDocument>();
        foreach (var _ in Enumerable.Range(0, 4)) docs.Add(NewStory(11001));
        foreach (var _ in Enumerable.Range(0, 6)) docs.Add(NewStory(11002));
        foreach (var id in new[] { 12001, 13001, 13002, 13003, 13004, 17001, 18001, 19002 })
            docs.Add(NewStory(id));
        await collection.InsertManyAsync(docs);

        var total = await StoryPagingHelper.CountDistinctStoriesAsync(collection, PinnedFilter);
        var fetched = await collection.Find(PinnedFilter)
            .SortByDescending(s => s.PinnedToTop).ThenByDescending(s => s.StoryId)
            .Limit(200).ToListAsync();
        var page = StoryPagingHelper.DeduplicatePage(fetched, limit: 100);
        var count = StoryPagingHelper.ResolveCount(
            total, page.Count, page.Count, limit: 100, isFirstPage: true);

        docs.Count.ShouldBe(18);
        total.ShouldBe(10);
        page.Count.ShouldBe(10);
        // Count matches the stories delivered, so the client stops after one request.
        count.ShouldBe(page.Count);
    }
}
