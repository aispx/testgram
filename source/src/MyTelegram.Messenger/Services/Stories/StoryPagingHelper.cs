using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Stories;

/// <summary>
/// Pagination arithmetic for profile story listings.
/// </summary>
/// <remarks>
/// <para>
/// A client that receives <c>stories.stories.count</c> larger than the number of stories it can
/// actually hold concludes the page was incomplete and asks for the next one. Pagination advances by
/// <c>StoryId</c>, so when the surplus comes from duplicate ids the follow-up request returns the
/// same page and the client re-requests forever — an observed ~8 req/s loop that never renders the
/// profile.
/// </para>
/// <para>
/// Two things inflate the count above what the client keeps: duplicate <c>StoryId</c> documents
/// (which the client collapses) and stories the privacy filter removes for this viewer. Both are
/// handled here so the reported count can never exceed what was actually delivered.
/// </para>
/// </remarks>
public static class StoryPagingHelper
{
    /// <summary>
    /// Number of distinct stories matching <paramref name="filter"/>, counting each
    /// <c>StoryId</c> once however many documents carry it.
    /// </summary>
    public static async Task<int> CountDistinctStoriesAsync(
        IMongoCollection<StoryDocument> collection,
        FilterDefinition<StoryDocument> filter)
    {
        var ids = await collection.DistinctAsync(s => s.StoryId, filter);
        return (await ids.ToListAsync()).Count;
    }

    /// <summary>
    /// Collapses documents sharing a <c>StoryId</c> — keeping the first, i.e. best-sorted, copy —
    /// and trims the result to <paramref name="limit"/>.
    /// </summary>
    public static List<StoryDocument> DeduplicatePage(IEnumerable<StoryDocument> stories, int limit) =>
        stories
            .GroupBy(s => s.StoryId)
            .Select(g => g.First())
            .Take(limit)
            .ToList();

    /// <summary>
    /// The <c>count</c> to report for a page.
    /// </summary>
    /// <param name="distinctTotal">Distinct stories matching the query, ignoring visibility.</param>
    /// <param name="deliveredCount">Stories actually placed in the response.</param>
    /// <param name="fetchedCount">Documents read for this page, after deduplication.</param>
    /// <param name="limit">Page size requested.</param>
    /// <param name="isFirstPage">Whether this request had no offset.</param>
    /// <remarks>
    /// Mid-listing the distinct total is the right answer — it tells the client how much is still to
    /// come. On the final page it can overshoot what the viewer may see, so the delivered count wins.
    /// On later pages the total is kept as a floor so the count never shrinks below the ids the
    /// client already holds from earlier pages.
    /// </remarks>
    public static int ResolveCount(
        int distinctTotal,
        int deliveredCount,
        int fetchedCount,
        int limit,
        bool isFirstPage)
    {
        var isLastPage = fetchedCount < limit;
        if (!isLastPage) return distinctTotal;
        return isFirstPage ? deliveredCount : Math.Max(distinctTotal, deliveredCount);
    }
}
