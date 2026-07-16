using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Stats;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Feature: stats-api, Property 17: The public-forward store content is exactly the deduped, public,
/// non-removed set.
///
/// For any sequence of record and remove operations, the Public_Forward_Store holds at most one entry per
/// distinct <c>(source, forwarding message/repost)</c> pair, holds an entry only when the forwarding
/// chat/channel has a public username, excludes entries whose forward (or source) has been removed, and
/// <c>count</c> equals the number of currently-held non-removed entries.
///
/// Validates: Requirements 11.1, 11.2, 11.3, 11.5, 11.6.
///
/// Per the tasks.md testing notes, storage property tests run against an in-memory store rather than a
/// real MongoDB. The production <see cref="PublicForwardStore"/> is MongoDB-backed via
/// <c>IMongoCollection</c>, so this file carries a self-contained in-memory
/// <see cref="InMemoryPublicForwardContentStore"/> (nested, uniquely named to avoid clashing with the
/// fakes defined by the sibling public-forward property files) that faithfully mirrors the documented
/// store write semantics: dedupe on <c>(source, fwdPeerId, fwdMsgId)</c> via upsert (which also clears a
/// prior soft-delete), soft-delete via <c>Removed</c>, and a non-removed <c>count</c> / page read over the
/// deterministic total order <c>(OrderKey, ForwardingPeerId, ForwardingMsgId)</c>.
///
/// The public-username check is the ingestion caller's responsibility (Requirement 11.5): the production
/// <c>PublicForwardIngestionSubscriber</c> only calls <c>RecordAsync</c> for public forwarders. This test
/// models the record path exactly as the ingestion does — a non-public forward event is never recorded —
/// so the "public username only" invariant is exercised at the boundary the store lives behind.
///
/// The shared <see cref="StatsGen.ForwardEventSequence"/> generator emits record/remove sequences whose
/// forwarding tuples are drawn from a small pool (so duplicates exercise dedup), some forwarders are
/// non-public (must not be recorded), and interleaved removes exercise soft-deletion. Each run executes a
/// minimum of 100 generated cases.
/// </summary>
[Properties(Arbitrary = new[] { typeof(StatsArbitraries) }, MaxTest = 100)]
public class PublicForwardStoreContentPropertyTests
{
    // A page size larger than any generated data set so a single read drains the whole store and lets us
    // assert on its entire content. The generator draws forwarding tuples from an 8x8 pool, so the live
    // set can never exceed 64 entries.
    private const int DrainLimit = 100;

    [Property]
    public void Store_content_is_exactly_the_deduped_public_non_removed_set(ForwardEventSequenceFixture sequence)
    {
        var store = new InMemoryPublicForwardContentStore();
        var source = new ForwardSourceKey(
            MapSourceType(sequence.SourceType),
            sequence.SourceOwnerPeerId,
            sequence.SourceItemId);

        // Apply the generated event stream through the store's documented write path: only public
        // forwarders are recorded (the ingestion caller filters non-public peers, Requirement 11.5);
        // removes soft-delete by forward ref (Requirement 11.6). Re-recording an existing pair upserts
        // (dedupe on the forwarding message, Requirements 11.1/11.2).
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

        // Independently replay the events to derive the expected non-removed, deduped, public-only set so
        // the assertions do not merely re-read the store under test.
        var expected = ComputeExpectedLiveSet(sequence.Events);

        // The full current content of the store for this source.
        var count = store.CountAsync(source).GetAwaiter().GetResult();
        var page = store.GetPageAsync(source, string.Empty, DrainLimit).GetAwaiter().GetResult();
        var held = page.Items;

        // count equals the number of currently-held non-removed entries (Requirement 11.3).
        count.ShouldBe(expected.Count);
        page.Count.ShouldBe(expected.Count);
        held.Count.ShouldBe(expected.Count);

        // At most one entry per distinct (source, forwarding message) pair — the held keys are unique
        // (Requirements 11.1, 11.2).
        var heldKeys = held
            .Select(r => (r.ForwardingPeerId, r.ForwardingMsgId))
            .ToList();
        heldKeys.Distinct().Count().ShouldBe(heldKeys.Count);

        // The held content equals exactly the expected deduped, public, non-removed set — no extra
        // entries (e.g. a non-public forwarder, Requirement 11.5, or a removed forward, Requirement 11.6)
        // and none missing.
        var heldSet = held
            .Select(r => (r.ForwardingPeerId, r.ForwardingMsgId, r.OrderKey))
            .ToHashSet();
        var expectedSet = expected
            .Select(r => (r.ForwardingPeerId, r.ForwardingMsgId, r.OrderKey))
            .ToHashSet();
        heldSet.ShouldBe(expectedSet, ignoreOrder: true);

        // Every held entry originates from a forward that was recorded as public and not subsequently
        // removed — i.e. it appears in the independently computed live set (Requirements 11.5, 11.6).
        foreach (var r in held)
        {
            expected.ShouldContain(e =>
                e.ForwardingPeerId == r.ForwardingPeerId
                && e.ForwardingMsgId == r.ForwardingMsgId
                && e.OrderKey == r.OrderKey);
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
    /// Replays the event stream to derive the expected set of currently-held forwards: deduped on the
    /// forwarding message, public forwarders only, and excluding any forward whose latest op is a remove.
    /// Mirrors the store's write semantics — a public record upserts (dedupe and clear any prior removal),
    /// a remove soft-deletes, and a non-public forwarder is never recorded.
    /// </summary>
    private static List<PublicForwardRecord> ComputeExpectedLiveSet(IReadOnlyList<ForwardEventFixture> events)
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

        return live.Values.ToList();
    }

    /// <summary>
    /// A self-contained in-memory <see cref="IPublicForwardStore"/> that faithfully mirrors the production
    /// <see cref="PublicForwardStore"/> content semantics without MongoDB. Uniquely named to avoid clashes
    /// with the fakes defined by the sibling public-forward property/unit test files.
    /// </summary>
    private sealed class InMemoryPublicForwardContentStore : IPublicForwardStore
    {
        private const int MaxLimit = 100;

        // Keyed by the dedupe id mirroring the production document _id: (source, fwdPeerId, fwdMsgId).
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

            if (limit <= 0)
            {
                return Task.FromResult(new PublicForwardPage(count, Array.Empty<PublicForwardRecord>(), null));
            }

            var pageSize = Math.Min(limit, MaxLimit);

            IEnumerable<PublicForwardRecord> candidates = ordered;
            if (!string.IsNullOrEmpty(offset))
            {
                var cursor = ParseCursor(offset);
                candidates = ordered.Where(r => Compare(r, cursor) > 0);
            }

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
