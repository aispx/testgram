using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Stats;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Feature: stats-api, Property 8: Pagination is stable across pages.
///
/// For any unchanged set of recorded public forwards and any page size, iterating pages via successive
/// <c>next_offset</c> values yields every forward exactly once in the store's deterministic total order
/// <c>(OrderKey, ForwardingPeerId, ForwardingMsgId)</c> — no forward repeated and none skipped — and the
/// concatenation of all pages equals the full ordered set.
///
/// Storage property tests run against an in-memory fake (no real MongoDB). The nested
/// <see cref="InMemoryPaginationForwardStore"/> below faithfully re-implements the
/// <see cref="PublicForwardStore"/> semantics exercised here: dedupe on
/// <c>(source, ForwardingPeerId, ForwardingMsgId)</c>, non-removed <c>Count</c>, stable
/// <c>(OrderKey, ForwardingPeerId, ForwardingMsgId)</c> ordering, <c>limit</c> clamp to <c>1..100</c>,
/// the <c>limit &lt;= 0</c> empty-page rule, and the <c>next_offset</c> cursor / fetch-one-extra logic.
///
/// Validates: Requirements 6.3, 7.3, 11.4.
/// </summary>
public class PaginationStabilityPropertyTests
{
    /// <summary>
    /// A generated pagination case: a set of distinct public-forward records for one source and a page
    /// size. Record counts span 0..250 (below, at, and well above the 1..100 page-size range) so paging
    /// with data both smaller and larger than the page size is exercised. Order keys are drawn from a
    /// small pool so ties occur and the secondary/tertiary tie-breakers
    /// (<c>ForwardingPeerId</c>, <c>ForwardingMsgId</c>) are meaningfully tested; <c>ForwardingMsgId</c>
    /// is unique per record, giving a strict deterministic total order.
    /// </summary>
    private sealed record PaginationCaseFixture(
        ForwardSourceKey Source,
        IReadOnlyList<PublicForwardRecord> Records,
        int PageSize);

    private static Gen<PaginationCaseFixture> PaginationCase =>
        from sourceType in Gen.Elements(ForwardSourceType.Message, ForwardSourceType.Story)
        from ownerPeerId in Gen.Choose(1, 20).Select(i => (long)i + 1000)
        from itemId in Gen.Choose(1, 20).Select(i => (long)i)
        from pageSize in Gen.Choose(1, 100)
        from recordCount in Gen.Choose(0, 250)
        from peerIds in StatsGen.ArrayOfLength(recordCount, Gen.Choose(1, 5).Select(i => (long)i + 5000))
        from orderKeys in StatsGen.ArrayOfLength(recordCount, Gen.Choose(1, 50).Select(i => (long)i))
        let records = Enumerable.Range(0, recordCount)
            .Select(i => new PublicForwardRecord(peerIds[i], i + 1, orderKeys[i]))
            .ToList()
        select new PaginationCaseFixture(
            new ForwardSourceKey(sourceType, ownerPeerId, itemId),
            records,
            pageSize);

    [Property(MaxTest = 100)]
    public Property Iterating_pages_yields_every_forward_exactly_once_in_total_order()
    {
        return Prop.ForAll(Arb.From(PaginationCase), testCase =>
        {
            var store = new InMemoryPaginationForwardStore();
            foreach (var record in testCase.Records)
            {
                store.RecordAsync(testCase.Source, record).GetAwaiter().GetResult();
            }

            // The store's deterministic total order. ForwardingMsgId is unique per record, so this is a
            // strict total order with no ambiguity.
            var expected = testCase.Records
                .OrderBy(r => r.OrderKey)
                .ThenBy(r => r.ForwardingPeerId)
                .ThenBy(r => r.ForwardingMsgId)
                .ToList();

            var collected = new List<PublicForwardRecord>();
            var seenOffsets = new HashSet<string>(StringComparer.Ordinal);
            var offset = string.Empty;
            var pageWithinLimit = true;
            var offsetsAdvanced = true;
            var iterations = 0;
            var maxIterations = testCase.Records.Count + 5;

            while (true)
            {
                var page = store.GetPageAsync(testCase.Source, offset, testCase.PageSize)
                    .GetAwaiter().GetResult();

                // No page may exceed the requested page size (which is <= 100 here, so no clamp applies).
                if (page.Items.Count > testCase.PageSize)
                {
                    pageWithinLimit = false;
                }

                collected.AddRange(page.Items);

                if (page.NextOffset is null)
                {
                    break;
                }

                // A well-formed cursor must always advance; a repeated offset would mean an infinite loop.
                if (!seenOffsets.Add(page.NextOffset) || ++iterations > maxIterations)
                {
                    offsetsAdvanced = false;
                    break;
                }

                offset = page.NextOffset;
            }

            // count reflects the full non-removed set.
            var count = store.CountAsync(testCase.Source).GetAwaiter().GetResult();

            // No forward repeated and none skipped: the concatenation equals the full ordered set exactly.
            var sameLength = collected.Count == expected.Count;
            var countMatches = count == expected.Count;
            var noDuplicates = collected
                .Select(r => (r.OrderKey, r.ForwardingPeerId, r.ForwardingMsgId))
                .Distinct()
                .Count() == collected.Count;
            var sameOrder = collected
                .Zip(expected, (a, b) =>
                    a.OrderKey == b.OrderKey
                    && a.ForwardingPeerId == b.ForwardingPeerId
                    && a.ForwardingMsgId == b.ForwardingMsgId)
                .All(equal => equal);

            return (pageWithinLimit && offsetsAdvanced && sameLength && countMatches
                    && noDuplicates && sameOrder)
                .Label($"records={expected.Count}, pageSize={testCase.PageSize}, " +
                       $"collected={collected.Count}, count={count}");
        });
    }

    /// <summary>
    /// A self-contained in-memory <see cref="IPublicForwardStore"/> that mirrors the paging-relevant
    /// semantics of the MongoDB-backed <see cref="PublicForwardStore"/> without any database dependency.
    /// Uniquely named to avoid clashing with fakes defined by sibling public-forward property tasks.
    /// </summary>
    private sealed class InMemoryPaginationForwardStore : IPublicForwardStore
    {
        private const int MaxLimit = 100;

        private sealed class Entry
        {
            public required ForwardSourceKey Source { get; init; }
            public required long ForwardingPeerId { get; init; }
            public required int ForwardingMsgId { get; init; }
            public required long OrderKey { get; set; }
            public bool Removed { get; set; }
        }

        // Keyed by the dedupe id: (source, ForwardingPeerId, ForwardingMsgId).
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

        public Task RecordAsync(ForwardSourceKey source, PublicForwardRecord record)
        {
            var id = BuildId(source, record.ForwardingPeerId, record.ForwardingMsgId);

            // Upsert on the dedupe key: at most one entry per (source, forwarding message); re-recording
            // refreshes the order key and clears any prior soft-delete.
            _entries[id] = new Entry
            {
                Source = source,
                ForwardingPeerId = record.ForwardingPeerId,
                ForwardingMsgId = record.ForwardingMsgId,
                OrderKey = record.OrderKey,
                Removed = false
            };

            return Task.CompletedTask;
        }

        public Task RemoveAsync(ForwardSourceKey source, ForwardRefKey forwardRef)
        {
            var id = BuildId(source, forwardRef.ForwardingPeerId, forwardRef.ForwardingMsgId);
            if (_entries.TryGetValue(id, out var entry))
            {
                entry.Removed = true;
            }

            return Task.CompletedTask;
        }

        public Task<int> CountAsync(ForwardSourceKey source)
        {
            var count = _entries.Values.Count(e => Matches(e, source));
            return Task.FromResult(count);
        }

        public Task<PublicForwardPage> GetPageAsync(ForwardSourceKey source, string offset, int limit)
        {
            var count = _entries.Values.Count(e => Matches(e, source));

            // limit <= 0 returns an empty page with the true total count (Requirements 6.6, 7.8).
            if (limit <= 0)
            {
                return Task.FromResult(new PublicForwardPage(count, [], null));
            }

            // Clamp the page size to at most 100 (Requirements 6.7).
            var pageSize = Math.Min(limit, MaxLimit);

            var ordered = _entries.Values
                .Where(e => Matches(e, source))
                .OrderBy(e => e.OrderKey)
                .ThenBy(e => e.ForwardingPeerId)
                .ThenBy(e => e.ForwardingMsgId)
                .AsEnumerable();

            if (!string.IsNullOrEmpty(offset))
            {
                var cursor = ParseCursor(offset);
                ordered = ordered.Where(e => IsAfterCursor(e, cursor));
            }

            // Fetch one extra document to determine whether a further page exists.
            var docs = ordered.Take(pageSize + 1).ToList();

            var hasMore = docs.Count > pageSize;
            var pageDocs = hasMore ? docs.Take(pageSize).ToList() : docs;

            var items = pageDocs
                .Select(e => new PublicForwardRecord(e.ForwardingPeerId, e.ForwardingMsgId, e.OrderKey))
                .ToList();

            string? nextOffset = null;
            if (hasMore)
            {
                var last = pageDocs[^1];
                nextOffset = BuildCursor(last.OrderKey, last.ForwardingPeerId, last.ForwardingMsgId);
            }

            return Task.FromResult(new PublicForwardPage(count, items, nextOffset));
        }

        private static bool Matches(Entry e, ForwardSourceKey source) =>
            !e.Removed
            && e.Source.Type == source.Type
            && e.Source.OwnerPeerId == source.OwnerPeerId
            && e.Source.ItemId == source.ItemId;

        private static bool IsAfterCursor(Entry e, (long OrderKey, long FwdPeerId, int FwdMsgId) cursor) =>
            e.OrderKey > cursor.OrderKey
            || (e.OrderKey == cursor.OrderKey && e.ForwardingPeerId > cursor.FwdPeerId)
            || (e.OrderKey == cursor.OrderKey
                && e.ForwardingPeerId == cursor.FwdPeerId
                && e.ForwardingMsgId > cursor.FwdMsgId);

        private static string BuildId(ForwardSourceKey source, long fwdPeerId, int fwdMsgId) =>
            $"{(int)source.Type}:{source.OwnerPeerId}:{source.ItemId}:{fwdPeerId}:{fwdMsgId}";

        private static string BuildCursor(long orderKey, long fwdPeerId, int fwdMsgId) =>
            $"{orderKey}:{fwdPeerId}:{fwdMsgId}";

        private static (long OrderKey, long FwdPeerId, int FwdMsgId) ParseCursor(string offset)
        {
            var parts = offset.Split(':');
            if (parts.Length != 3
                || !long.TryParse(parts[0], out var orderKey)
                || !long.TryParse(parts[1], out var fwdPeerId)
                || !int.TryParse(parts[2], out var fwdMsgId))
            {
                throw new InvalidStatsOffsetException(offset);
            }

            return (orderKey, fwdPeerId, fwdMsgId);
        }
    }
}
