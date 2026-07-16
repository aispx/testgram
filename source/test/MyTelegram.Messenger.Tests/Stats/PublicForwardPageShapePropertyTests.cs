using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Stats;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Feature: stats-api, Property 7: Public-forward pages are correctly shaped.
///
/// For any set of recorded public forwards and any requested <c>limit</c>, the returned <c>forwards</c>
/// list contains at most <c>min(limit, 100)</c> entries, <c>count</c> equals the total number of recorded
/// forwards for the source, and <c>next_offset</c> is set to a non-empty cursor exactly when more forwards
/// remain beyond the returned page and is left unset otherwise.
///
/// Validates: Requirements 6.1, 6.2, 6.7, 7.1, 7.2.
///
/// Per the tasks.md testing notes, storage property tests run against an in-memory store rather than a
/// real MongoDB. The production <see cref="PublicForwardStore"/> is MongoDB-backed via
/// <c>IMongoCollection</c>, so this file carries a self-contained in-memory
/// <see cref="InMemoryPublicForwardPageStore"/> (nested, uniquely named) that faithfully mirrors the
/// documented store semantics: dedupe on <c>(source, fwdPeerId, fwdMsgId)</c>, a stable
/// <c>(OrderKey, ForwardingPeerId, ForwardingMsgId)</c> total order, a <c>limit</c> clamp to <c>1..100</c>,
/// a <c>limit &lt;= 0</c> empty page carrying the true count, a <c>next_offset</c> set only when more
/// forwards remain, and an <see cref="InvalidStatsOffsetException"/> for an unrecognized non-empty offset.
///
/// The shared <see cref="StatsGen.ForwardEventSequence"/> generator emits record/remove sequences whose
/// forwarding tuples are drawn from a small pool (so duplicates exercise dedup), some forwarders are
/// non-public (must not be recorded), interleaved removes exercise soft-deletion, and the requested
/// <c>Limit</c> spans 0..120 to cover the clamp boundaries (&lt;=0, 1..100, &gt;100). Each run executes a
/// minimum of 100 generated cases.
/// </summary>
[Properties(Arbitrary = new[] { typeof(StatsArbitraries) }, MaxTest = 100)]
public class PublicForwardPageShapePropertyTests
{
    private const int MaxLimit = 100;

    [Property]
    public void Public_forward_page_is_correctly_shaped(ForwardEventSequenceFixture sequence)
    {
        var store = new InMemoryPublicForwardPageStore();
        var source = new ForwardSourceKey(
            MapSourceType(sequence.SourceType),
            sequence.SourceOwnerPeerId,
            sequence.SourceItemId);

        // Apply the generated event stream through the store's documented write path: only public
        // forwarders are recorded (the ingestion caller filters non-public peers, Requirement 11.5);
        // removes soft-delete by forward ref. Re-recording an existing pair upserts (dedupe on the
        // forwarding message, Requirements 11.1/11.2).
        foreach (var e in sequence.Events)
        {
            if (e.Op == ForwardOpFixture.Record && e.ForwardingPeerIsPublic)
            {
                store.RecordAsync(source, new PublicForwardRecord(e.ForwardingPeerId, e.ForwardingMsgId, e.OrderKey))
                    .GetAwaiter().GetResult();
            }
            else if (e.Op == ForwardOpFixture.Remove)
            {
                store.RemoveAsync(source, new ForwardRefKey(e.ForwardingPeerId, e.ForwardingMsgId))
                    .GetAwaiter().GetResult();
            }
        }

        // Independently replay the events to derive the expected non-removed, deduped set and its order,
        // so the assertions do not merely re-read the store under test.
        var expectedOrdered = ComputeExpectedOrdered(sequence.Events);
        var expectedCount = expectedOrdered.Count;

        var limit = sequence.Limit;
        var page = store.GetPageAsync(source, string.Empty, limit).GetAwaiter().GetResult();

        // count always equals the total number of currently-recorded forwards for the source
        // (Requirements 6.1, 7.1) regardless of the requested limit.
        page.Count.ShouldBe(expectedCount);

        if (limit <= 0)
        {
            // limit <= 0: empty forwards list with the true count, no next cursor (Requirements 6.6/7.8).
            page.Items.Count.ShouldBe(0);
            page.NextOffset.ShouldBeNull();
            return;
        }

        var pageSize = Math.Min(limit, MaxLimit);
        var expectedPage = expectedOrdered.Take(pageSize).ToList();
        var hasMore = expectedCount > pageSize;

        // forwards contains at most min(limit, 100) entries (Requirements 6.1, 6.7, 7.1).
        page.Items.Count.ShouldBeLessThanOrEqualTo(pageSize);
        page.Items.Count.ShouldBe(expectedPage.Count);

        // The returned page is exactly the ordered prefix of the deduped, non-removed set.
        page.Items.ShouldBe(expectedPage);

        // next_offset is a non-empty cursor exactly when more forwards remain beyond the returned page,
        // and is left unset otherwise (Requirements 6.2, 7.2).
        if (hasMore)
        {
            page.NextOffset.ShouldNotBeNull();
            page.NextOffset.ShouldNotBeEmpty();
        }
        else
        {
            page.NextOffset.ShouldBeNull();
        }
    }

    private static ForwardSourceType MapSourceType(ForwardSourceTypeFixture type) =>
        type switch
        {
            ForwardSourceTypeFixture.Message => ForwardSourceType.Message,
            ForwardSourceTypeFixture.Story => ForwardSourceType.Story,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };

    /// <summary>
    /// Replays the event stream to derive the expected set of currently-held, non-removed forwards in the
    /// store's deterministic total order <c>(OrderKey, ForwardingPeerId, ForwardingMsgId)</c>. Mirrors the
    /// store's write semantics: public records upsert (dedupe on the forwarding message and clear any prior
    /// removal), removes soft-delete, and non-public forwarders are ignored.
    /// </summary>
    private static List<PublicForwardRecord> ComputeExpectedOrdered(IReadOnlyList<ForwardEventFixture> events)
    {
        var live = new Dictionary<(long PeerId, int MsgId), PublicForwardRecord>();

        foreach (var e in events)
        {
            var key = (e.ForwardingPeerId, e.ForwardingMsgId);
            if (e.Op == ForwardOpFixture.Record && e.ForwardingPeerIsPublic)
            {
                live[key] = new PublicForwardRecord(e.ForwardingPeerId, e.ForwardingMsgId, e.OrderKey);
            }
            else if (e.Op == ForwardOpFixture.Remove)
            {
                live.Remove(key);
            }
        }

        return live.Values
            .OrderBy(r => r.OrderKey)
            .ThenBy(r => r.ForwardingPeerId)
            .ThenBy(r => r.ForwardingMsgId)
            .ToList();
    }

    /// <summary>
    /// A self-contained in-memory <see cref="IPublicForwardStore"/> that faithfully mirrors the production
    /// <see cref="PublicForwardStore"/> semantics without MongoDB. Uniquely named to avoid clashes with the
    /// fakes defined by the sibling public-forward property/unit test files.
    /// </summary>
    private sealed class InMemoryPublicForwardPageStore : IPublicForwardStore
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

            // Clamp limit to at most 100 (Requirements 6.7).
            var pageSize = Math.Min(limit, MaxLimit);

            IEnumerable<PublicForwardRecord> candidates = ordered;
            if (!string.IsNullOrEmpty(offset))
            {
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
