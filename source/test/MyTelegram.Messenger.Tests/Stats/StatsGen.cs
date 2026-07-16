using FsCheck;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Central catalogue of FsCheck generators for the stats-api feature. Every later property test composes
/// these (directly, or via <see cref="StatsArbitraries"/>) so the input space — channels/supergroups,
/// per-day metric data, graph series/specs, forward-event sequences, and async tokens — is defined and
/// constrained in exactly one place.
///
/// Feature: stats-api. These generators back Properties 1-17 (see <see cref="StatsTestModels"/> for the
/// fixture types they emit and which property each supports).
/// </summary>
public static class StatsGen
{
    // Day 0 (00:00:00 UTC) for building aligned Unix-second day keys.
    private const int SecondsPerDay = 86_400;

    // A fixed base day (2023-08-01 00:00:00 UTC) keeps generated timestamps realistic and aligned.
    private const int BaseUtcDay = 1_690_848_000;

    // ---- Primitive id / scalar generators ----------------------------------------------------

    /// <summary>Positive 64-bit identifier drawn from a small-ish range.</summary>
    public static Gen<long> PositiveId => Gen.Choose(1, 1_000_000).Select(i => (long)i);

    /// <summary>Identifier drawn from a tiny pool so overlaps/collisions across entities are likely.</summary>
    public static Gen<long> PooledId => Gen.Choose(1, 20).Select(i => (long)i);

    /// <summary>A Unix-second timestamp aligned to 00:00:00 UTC, within a ~1 year window of the base day.</summary>
    public static Gen<int> AlignedUtcDay =>
        Gen.Choose(0, 365).Select(offset => BaseUtcDay + offset * SecondsPerDay);

    /// <summary>Reporting window in days, clamped to the valid 1..365 range (default is 7).</summary>
    public static Gen<int> ReportingWindowDays => Gen.Choose(1, 365);

    /// <summary>A public username (non-empty) — a channel is public iff it has one.</summary>
    public static Gen<string> PublicUserName =>
        Gen.Choose(1, 1_000_000).Select(i => "channel_" + i);

    // ---- Fixed-length array helper (independent of FsCheck-version array overloads) -----------

    /// <summary>Generates a fixed-length array by chaining <paramref name="length"/> draws of the element.</summary>
    public static Gen<T[]> ArrayOfLength<T>(int length, Gen<T> element)
    {
        var acc = Gen.Constant(Array.Empty<T>());
        for (var i = 0; i < length; i++)
        {
            acc = acc.SelectMany(arr => element.Select(x =>
            {
                var next = new T[arr.Length + 1];
                Array.Copy(arr, next, arr.Length);
                next[arr.Length] = x;
                return next;
            }));
        }

        return acc;
    }

    // ---- Channels / supergroups --------------------------------------------------------------

    private static Gen<StatsChannelFixture> Channel(StatsChannelKindFixture kind, bool forcePublic)
    {
        return from channelId in PooledId.Select(id => id + 1000)
               from creatorId in PooledId
               from adminCount in Gen.Choose(0, 4)
               from admins in ArrayOfLength(adminCount, PooledId)
               from participants in Gen.Choose(0, 1_000_000)
               from isPublic in (forcePublic ? Gen.Constant(true) : Arb.Generate<bool>())
               from userName in (isPublic ? PublicUserName.Select(u => (string?)u) : Gen.Constant<string?>(null))
               select new StatsChannelFixture(
                   channelId,
                   kind,
                   userName,
                   creatorId,
                   admins.Distinct().ToList(),
                   participants);
    }

    /// <summary>A broadcast channel (public or private).</summary>
    public static Gen<StatsChannelFixture> BroadcastChannel =>
        Channel(StatsChannelKindFixture.Broadcast, forcePublic: false);

    /// <summary>A supergroup / megagroup (public or private).</summary>
    public static Gen<StatsChannelFixture> Supergroup =>
        Channel(StatsChannelKindFixture.Megagroup, forcePublic: false);

    /// <summary>Either a broadcast channel or a supergroup.</summary>
    public static Gen<StatsChannelFixture> AnyChannel =>
        Gen.OneOf(BroadcastChannel, Supergroup);

    // ---- Access-control cases ----------------------------------------------------------------

    private static Gen<CallerKindFixture> CallerKind =>
        Gen.Elements(CallerKindFixture.User, CallerKindFixture.Bot, CallerKindFixture.Anonymous);

    /// <summary>
    /// An access-control case whose violable conditions (caller type, target resolution, kind,
    /// joinability — private channel + non-member, and admin) are toggled independently so the
    /// ordered-first-failure property can be checked across the full lattice of violations.
    /// </summary>
    public static Gen<StatsAccessCaseFixture> AccessCase =>
        from caller in CallerKind
        from callerUserId in PooledId
        from targetResolves in Arb.Generate<bool>()
        from requiredKind in Gen.Elements(StatsChannelKindFixture.Broadcast, StatsChannelKindFixture.Megagroup)
        from channelKind in Gen.Elements(StatsChannelKindFixture.Broadcast, StatsChannelKindFixture.Megagroup)
        from channel in Channel(channelKind, forcePublic: false)
        from checkJoinable in Arb.Generate<bool>()
        from callerIsMember in Arb.Generate<bool>()
        from callerIsAdmin in Arb.Generate<bool>()
        select new StatsAccessCaseFixture(
            caller,
            callerUserId,
            targetResolves,
            targetResolves ? channel : null,
            requiredKind,
            checkJoinable,
            callerIsMember,
            callerIsAdmin);

    // ---- Per-day metric data -----------------------------------------------------------------

    /// <summary>
    /// A sparse per-day metric series with unique, ascending day keys and non-negative values. Gaps
    /// between recorded days exercise zero-fill in aggregation and per-item series reproduction.
    /// </summary>
    public static Gen<DailyMetricSeriesFixture> MetricSeries =>
        from metric in Gen.Elements("followers", "views", "shares", "reactions", "muted", "messages", "members")
        from window in ReportingWindowDays
        from count in Gen.Choose(0, 30)
        from days in ArrayOfLength(count, AlignedUtcDay)
        from values in ArrayOfLength(count, Gen.Choose(0, 100_000).Select(i => (long)i))
        select new DailyMetricSeriesFixture(
            metric,
            days.Distinct()
                .OrderBy(d => d)
                .Zip(values, (day, value) => new DailyMetricPointFixture(day, value))
                .ToList(),
            window);

    // ---- Graph series / specs ----------------------------------------------------------------

    private static readonly string[] ColorKeys =
        { "primary", "secondary", "tertiary", "quaternary", "quinary" };

    /// <summary>A strictly-ascending sequence of <paramref name="count"/> Unix-millisecond timestamps.</summary>
    private static Gen<long[]> AscendingMillis(int count) =>
        from start in Gen.Choose(0, 365).Select(o => (long)(BaseUtcDay + o * SecondsPerDay) * 1000L)
        from steps in ArrayOfLength(count, Gen.Choose(1, 5))
        select steps
            .Aggregate(
                new List<long> { start },
                (acc, step) =>
                {
                    acc.Add(acc[^1] + (long)step * SecondsPerDay * 1000L);
                    return acc;
                })
            // drop the seed when count == 0 so an empty x axis is genuinely empty
            .Skip(count == 0 ? 1 : 0)
            .Take(count)
            .ToArray();

    private static Gen<GraphSpecFixture> GraphSpecOfLength(int pointCount, int seriesCount, bool dark)
    {
        return from x in AscendingMillis(pointCount)
               from seriesDefs in ArrayOfLength(
                   seriesCount,
                   from color in Gen.Elements(ColorKeys)
                   from values in ArrayOfLength(pointCount, Gen.Choose(0, 100_000).Select(i => (long)i))
                   select (color, values))
               select new GraphSpecFixture(
                   x,
                   seriesDefs
                       .Select((s, idx) => new GraphSeriesFixture(
                           "y" + idx,
                           "Series " + idx,
                           s.color,
                           s.values))
                       .ToList(),
                   dark);
    }

    /// <summary>
    /// A statistics-graph spec covering empty, single-series, multi-series, and zoom cases, with the
    /// theme flag toggled. Series value counts always match the x-axis length.
    /// </summary>
    public static Gen<GraphSpecFixture> GraphSpec =>
        from dark in Arb.Generate<bool>()
        from pointCount in Gen.Frequency(
            Tuple.Create(1, Gen.Constant(0)),        // empty graph
            Tuple.Create(4, Gen.Choose(1, 20)))      // populated graph
        from seriesCount in Gen.Choose(1, 4)
        from includeZoom in Arb.Generate<bool>()
        from spec in GraphSpecOfLength(pointCount, seriesCount, dark)
        from zoom in (includeZoom && pointCount > 0
            ? GraphSpecOfLength(Math.Max(1, pointCount), seriesCount, dark).Select(z => (GraphSpecFixture?)z)
            : Gen.Constant<GraphSpecFixture?>(null))
        select spec with { Zoom = zoom };

    // ---- Forward-event sequences -------------------------------------------------------------

    /// <summary>
    /// A sequence of record/remove forward events for a single source. Forwarding tuples are drawn from a
    /// small pool so duplicates arise (dedup), some forwarders are non-public (must not be recorded), and
    /// interleaved removes exercise soft-deletion. The requested page <c>Limit</c> spans 0..120 to cover
    /// the clamp boundaries (<=0, 1..100, >100).
    /// </summary>
    public static Gen<ForwardEventSequenceFixture> ForwardEventSequence =>
        from sourceType in Gen.Elements(ForwardSourceTypeFixture.Message, ForwardSourceTypeFixture.Story)
        from ownerPeerId in PooledId.Select(id => id + 1000)
        from sourceItemId in PooledId
        from count in Gen.Choose(0, 40)
        from events in ArrayOfLength(
            count,
            from op in Gen.Frequency(
                Tuple.Create(4, Gen.Constant(ForwardOpFixture.Record)),
                Tuple.Create(1, Gen.Constant(ForwardOpFixture.Remove)))
            from fwdPeerId in Gen.Choose(1, 8).Select(i => (long)i + 5000)
            from fwdMsgId in Gen.Choose(1, 8)
            from isPublic in Gen.Frequency(
                Tuple.Create(4, Gen.Constant(true)),
                Tuple.Create(1, Gen.Constant(false)))
            from orderKey in Gen.Choose(1, 1_000_000).Select(i => (long)i)
            select (op, fwdPeerId, fwdMsgId, isPublic, orderKey))
        from limit in Gen.Choose(0, 120)
        select new ForwardEventSequenceFixture(
            sourceType,
            ownerPeerId,
            sourceItemId,
            events
                .Select(e => new ForwardEventFixture(
                    e.op,
                    sourceType,
                    ownerPeerId,
                    sourceItemId,
                    e.fwdPeerId,
                    e.fwdMsgId,
                    e.isPublic,
                    e.orderKey))
                .ToList(),
            limit);

    // ---- Async tokens ------------------------------------------------------------------------

    /// <summary>An opaque random token string.</summary>
    public static Gen<string> OpaqueToken =>
        from n in Gen.Choose(16, 32)
        from chars in ArrayOfLength(n, Gen.Elements("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".ToCharArray()))
        select new string(chars);

    /// <summary>
    /// An async-graph token fixture with the unrecognized / expired / outdated conditions toggled
    /// independently (so the fixed error precedence can be checked), an issued/now pair straddling the
    /// 86,400-second validity window, and an optional zoom series with a matching or absent zoom x.
    /// </summary>
    public static Gen<AsyncTokenFixture> AsyncToken =>
        from token in OpaqueToken
        from isRecognized in Arb.Generate<bool>()
        from isExpired in Arb.Generate<bool>()
        from isOutdated in Arb.Generate<bool>()
        from issuedAt in AlignedUtcDay
        from ageSeconds in (isExpired
            ? Gen.Choose(AsyncTokenFixture.ValidityWindowSeconds + 1, AsyncTokenFixture.ValidityWindowSeconds + SecondsPerDay)
            : Gen.Choose(0, AsyncTokenFixture.ValidityWindowSeconds))
        from spec in GraphSpec
        from includeZoom in Arb.Generate<bool>()
        from zoomSpec in (includeZoom
            ? GraphSpec.Select(z => (GraphSpecFixture?)z)
            : Gen.Constant<GraphSpecFixture?>(null))
        from zoomX in (includeZoom
            ? Gen.Choose(0, 1000).Select(i => (long?)i)
            : Gen.Constant<long?>(null))
        select new AsyncTokenFixture(
            token,
            isRecognized,
            isExpired,
            isOutdated,
            issuedAt,
            issuedAt + ageSeconds,
            spec,
            zoomSpec,
            zoomX);
}
