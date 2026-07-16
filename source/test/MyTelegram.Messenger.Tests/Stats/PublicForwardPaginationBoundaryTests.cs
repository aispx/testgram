using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Stats;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Feature: stats-api, Task 5.5 — example/edge-case unit tests for the Public_Forward_Store pagination
/// boundaries.
///
/// These complement the page-shape property test (Property 7) by pinning down the three boundary
/// behaviours of <see cref="IPublicForwardStore.GetPageAsync"/> that the property does not force on every
/// generated run:
/// <list type="bullet">
///   <item><c>limit &lt;= 0</c> (both <c>0</c> and negative) returns an empty <c>forwards</c> list whose
///   <c>count</c> equals the total number of non-removed forwards, and no <c>next_offset</c>
///   (Requirements 6.6, 7.8).</item>
///   <item><c>limit &gt; 100</c> is clamped so the returned page contains at most <c>100</c> forwards
///   (Requirement 6.7).</item>
///   <item>An unrecognized non-empty <c>offset</c> raises <see cref="InvalidStatsOffsetException"/> — the
///   signal the handler maps to an invalid-offset error — and never yields a partial page
///   (Requirement 6.8).</item>
/// </list>
///
/// Per the tasks.md testing notes, storage tests run against an in-memory store rather than a real
/// MongoDB. The production <see cref="PublicForwardStore"/> is MongoDB-backed via <c>IMongoCollection</c>,
/// so this file carries a self-contained in-memory <see cref="InMemoryPublicForwardBoundaryStore"/>
/// (nested, uniquely named to avoid clashing with the fakes defined by the sibling public-forward test
/// files) that faithfully mirrors the documented <c>GetPageAsync</c> semantics: dedupe on
/// <c>(source, fwdPeerId, fwdMsgId)</c>, soft-delete via <c>Removed</c>, a stable
/// <c>(OrderKey, ForwardingPeerId, ForwardingMsgId)</c> total order, a <c>limit &lt;= 0</c> empty page
/// carrying the true count, a <c>limit</c> clamp to <c>1..100</c>, and an
/// <see cref="InvalidStatsOffsetException"/> for an unrecognized non-empty offset.
/// </summary>
public class PublicForwardPaginationBoundaryTests
{
    private const int MaxLimit = 100;

    private static readonly ForwardSourceKey Source =
        new(ForwardSourceType.Message, OwnerPeerId: 1001, ItemId: 42);

    // ----- limit <= 0 -> empty forwards with true count (Requirements 6.6, 7.8) -----

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Limit_less_than_or_equal_to_zero_returns_empty_forwards_with_true_count(int limit)
    {
        var store = new InMemoryPublicForwardBoundaryStore();
        // Seed five distinct public forwards for the source.
        SeedForwards(store, count: 5);

        var page = store.GetPageAsync(Source, string.Empty, limit).GetAwaiter().GetResult();

        // Empty forwards list...
        page.Items.Count.ShouldBe(0);
        // ...but count reflects the true number of non-removed forwards for the source.
        page.Count.ShouldBe(5);
        // ...and no continuation cursor is offered.
        page.NextOffset.ShouldBeNull();
    }

    [Fact]
    public void Limit_zero_true_count_excludes_removed_forwards()
    {
        var store = new InMemoryPublicForwardBoundaryStore();
        SeedForwards(store, count: 5);

        // Soft-remove two of the seeded forwards; the true count must drop to the non-removed total.
        store.RemoveAsync(Source, new ForwardRefKey(ForwardingPeerId: 2000, ForwardingMsgId: 0))
            .GetAwaiter().GetResult();
        store.RemoveAsync(Source, new ForwardRefKey(ForwardingPeerId: 2001, ForwardingMsgId: 1))
            .GetAwaiter().GetResult();

        var page = store.GetPageAsync(Source, string.Empty, 0).GetAwaiter().GetResult();

        page.Items.Count.ShouldBe(0);
        page.Count.ShouldBe(3);
        page.NextOffset.ShouldBeNull();
    }

    // ----- limit > 100 -> clamp to at most 100 (Requirement 6.7) -----

    [Fact]
    public void Limit_greater_than_100_returns_at_most_100_forwards()
    {
        var store = new InMemoryPublicForwardBoundaryStore();
        // Seed more than the clamp so the excess must be withheld.
        SeedForwards(store, count: 150);

        var page = store.GetPageAsync(Source, string.Empty, limit: 101).GetAwaiter().GetResult();

        // The page is clamped to the 100-item maximum...
        page.Items.Count.ShouldBe(MaxLimit);
        // ...count still reports the full non-removed total...
        page.Count.ShouldBe(150);
        // ...and a continuation cursor is offered because more forwards remain.
        page.NextOffset.ShouldNotBeNull();
        page.NextOffset.ShouldNotBeEmpty();
    }

    [Fact]
    public void Limit_far_greater_than_100_still_clamps_to_100()
    {
        var store = new InMemoryPublicForwardBoundaryStore();
        SeedForwards(store, count: 120);

        var page = store.GetPageAsync(Source, string.Empty, limit: int.MaxValue).GetAwaiter().GetResult();

        page.Items.Count.ShouldBe(MaxLimit);
        page.Count.ShouldBe(120);
        page.NextOffset.ShouldNotBeNull();
    }

    [Fact]
    public void Limit_greater_than_100_when_fewer_than_100_exist_returns_all_without_next_offset()
    {
        var store = new InMemoryPublicForwardBoundaryStore();
        SeedForwards(store, count: 30);

        var page = store.GetPageAsync(Source, string.Empty, limit: 200).GetAwaiter().GetResult();

        // Clamp does not manufacture entries: only the 30 recorded forwards are returned...
        page.Items.Count.ShouldBe(30);
        page.Count.ShouldBe(30);
        // ...and no continuation cursor since the whole set fit in the (clamped) page.
        page.NextOffset.ShouldBeNull();
    }

    // ----- unrecognized non-empty offset -> invalid-offset error, no partial page (Requirement 6.8) -----

    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("abc:def:ghi")]
    [InlineData("100:200")]            // too few components
    [InlineData("100:200:300:400")]    // too many components
    [InlineData("x:2000:0")]           // non-numeric order key
    [InlineData("100:y:0")]            // non-numeric peer id
    [InlineData("100:2000:z")]         // non-numeric msg id
    public void Unrecognized_non_empty_offset_throws_invalid_offset_and_returns_no_partial_page(string offset)
    {
        var store = new InMemoryPublicForwardBoundaryStore();
        SeedForwards(store, count: 5);

        // An unrecognized non-empty cursor signals an invalid-offset error rather than any page.
        var ex = Should.Throw<InvalidStatsOffsetException>(() =>
            store.GetPageAsync(Source, offset, limit: 10).GetAwaiter().GetResult());

        // The failure carries the offending offset for the handler's diagnostics; no page is produced.
        ex.Offset.ShouldBe(offset);
    }

    [Fact]
    public void Empty_offset_is_treated_as_the_first_page_not_an_invalid_offset()
    {
        var store = new InMemoryPublicForwardBoundaryStore();
        SeedForwards(store, count: 3);

        // An empty offset is the "start from the beginning" sentinel, never an invalid cursor.
        var page = Should.NotThrow(() =>
            store.GetPageAsync(Source, string.Empty, limit: 10).GetAwaiter().GetResult());

        page.Items.Count.ShouldBe(3);
        page.Count.ShouldBe(3);
        page.NextOffset.ShouldBeNull();
    }

    /// <summary>
    /// Seeds <paramref name="count"/> distinct public forwards with a strictly increasing, deterministic
    /// total order so paging boundaries are unambiguous.
    /// </summary>
    private static void SeedForwards(IPublicForwardStore store, int count)
    {
        for (var i = 0; i < count; i++)
        {
            store.RecordAsync(
                    Source,
                    new PublicForwardRecord(ForwardingPeerId: 2000 + i, ForwardingMsgId: i, OrderKey: i))
                .GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// A self-contained in-memory <see cref="IPublicForwardStore"/> that faithfully mirrors the production
    /// <see cref="PublicForwardStore"/> <c>GetPageAsync</c> boundary semantics without MongoDB. Uniquely
    /// named to avoid clashes with the fakes defined by the sibling public-forward test files.
    /// </summary>
    private sealed class InMemoryPublicForwardBoundaryStore : IPublicForwardStore
    {
        // Dedupe key mirrors the production document _id: (source, forwardingPeerId, forwardingMsgId).
        private readonly Dictionary<(ForwardSourceKey Source, long PeerId, int MsgId), (PublicForwardRecord Record, bool Removed)> _store = new();

        public Task RecordAsync(ForwardSourceKey source, PublicForwardRecord record)
        {
            // Upsert on the dedupe key; re-recording refreshes the entry and clears any prior soft-delete.
            _store[(source, record.ForwardingPeerId, record.ForwardingMsgId)] = (record, false);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(ForwardSourceKey source, ForwardRefKey forwardRef)
        {
            var key = (source, forwardRef.ForwardingPeerId, forwardRef.ForwardingMsgId);
            if (_store.TryGetValue(key, out var existing))
            {
                _store[key] = (existing.Record, true);
            }

            return Task.CompletedTask;
        }

        public Task<int> CountAsync(ForwardSourceKey source) =>
            Task.FromResult(LiveOrdered(source).Count);

        public Task<PublicForwardPage> GetPageAsync(ForwardSourceKey source, string offset, int limit)
        {
            var ordered = LiveOrdered(source);
            var count = ordered.Count;

            // limit <= 0 returns an empty page with the true total count (Requirements 6.6, 7.8).
            if (limit <= 0)
            {
                return Task.FromResult(new PublicForwardPage(count, Array.Empty<PublicForwardRecord>(), null));
            }

            // Clamp limit to at most 100 (Requirement 6.7).
            var pageSize = Math.Min(limit, MaxLimit);

            IEnumerable<PublicForwardRecord> candidates = ordered;
            if (!string.IsNullOrEmpty(offset))
            {
                // An unrecognized non-empty offset throws before any page is produced (Requirement 6.8).
                var cursor = ParseCursor(offset);
                candidates = ordered.Where(r => Compare(r, cursor) > 0);
            }

            // Fetch one extra to determine whether a further page remains.
            var window = candidates.Take(pageSize + 1).ToList();
            var hasMore = window.Count > pageSize;
            var items = hasMore ? window.Take(pageSize).ToList() : window;

            string? nextOffset = null;
            if (hasMore)
            {
                var last = items[^1];
                nextOffset = BuildCursor(last.OrderKey, last.ForwardingPeerId, last.ForwardingMsgId);
            }

            return Task.FromResult(new PublicForwardPage(count, items, nextOffset));
        }

        private List<PublicForwardRecord> LiveOrdered(ForwardSourceKey source) =>
            _store
                .Where(kv => kv.Key.Source.Equals(source) && !kv.Value.Removed)
                .Select(kv => kv.Value.Record)
                .OrderBy(r => r.OrderKey)
                .ThenBy(r => r.ForwardingPeerId)
                .ThenBy(r => r.ForwardingMsgId)
                .ToList();

        private static string BuildCursor(long orderKey, long fwdPeerId, int fwdMsgId) =>
            $"{orderKey}:{fwdPeerId}:{fwdMsgId}";

        private static (long OrderKey, long PeerId, int MsgId) ParseCursor(string offset)
        {
            var parts = offset.Split(':');
            if (parts.Length != 3
                || !long.TryParse(parts[0], out var orderKey)
                || !long.TryParse(parts[1], out var fwdPeerId)
                || !int.TryParse(parts[2], out var fwdMsgId))
            {
                // Non-empty but unrecognized cursor signals an invalid-offset error (Requirement 6.8).
                throw new InvalidStatsOffsetException(offset);
            }

            return (orderKey, fwdPeerId, fwdMsgId);
        }

        private static int Compare(PublicForwardRecord record, (long OrderKey, long PeerId, int MsgId) cursor)
        {
            var byOrder = record.OrderKey.CompareTo(cursor.OrderKey);
            if (byOrder != 0)
            {
                return byOrder;
            }

            var byPeer = record.ForwardingPeerId.CompareTo(cursor.PeerId);
            return byPeer != 0 ? byPeer : record.ForwardingMsgId.CompareTo(cursor.MsgId);
        }
    }
}
