namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Shared, self-describing DTO/fixture types produced by the stats-api FsCheck generators
/// (see <see cref="StatsGen"/>). They intentionally live in the test project rather than referencing
/// the production <c>MyTelegram.Messenger/Services/Stats</c> value types so that the generator surface
/// is stable and each later property task (Properties 1-20) can map these fixtures onto whatever the
/// component under test requires. Every fixture carries enough classification metadata to let a
/// property assert the expected outcome without re-deriving it.
///
/// Feature: stats-api. Used by the property tasks: access control (1), recent posts (2),
/// enabled-notifications (3), top entities (4), theme palette (5), per-item series (6),
/// public-forward pages (7), pagination stability (8), referenced chats/users (9),
/// graph JSON structure (10), graph JSON round-trip (11), async token round-trip/zoom (12),
/// unrecognized token (13), async error precedence (14), period aggregation (15),
/// reporting period (16), public-forward store content (17).
/// </summary>

/// <summary>The two channel kinds the stats methods distinguish (broadcast vs. megagroup/supergroup).</summary>
public enum StatsChannelKindFixture
{
    Broadcast,
    Megagroup
}

/// <summary>
/// A channel/supergroup fixture with the fields the Access_Controller and Stats_Service read:
/// kind, public username (public iff non-empty), admin list, creator, and participant count.
/// </summary>
public sealed record StatsChannelFixture(
    long ChannelId,
    StatsChannelKindFixture Kind,
    string? UserName,
    long CreatorId,
    IReadOnlyList<long> AdminUserIds,
    int ParticipantsCount)
{
    public bool IsPublic => !string.IsNullOrEmpty(UserName);
    public bool IsBroadcast => Kind == StatsChannelKindFixture.Broadcast;
    public bool IsMegagroup => Kind == StatsChannelKindFixture.Megagroup;

    public bool IsAdmin(long userId) => userId == CreatorId || AdminUserIds.Contains(userId);

    public override string ToString() =>
        $"Channel({ChannelId}, {Kind}, public={IsPublic}, admins=[{string.Join(",", AdminUserIds)}], " +
        $"creator={CreatorId}, participants={ParticipantsCount})";
}

/// <summary>How the caller of a stats request is classified for access-control tests.</summary>
public enum CallerKindFixture
{
    User,
    Bot,
    Anonymous
}

/// <summary>
/// A caller + target pairing for access-control property tests. The independent boolean toggles let a
/// generator switch each violable access condition on/off so Property 1 can assert the first-failure order.
/// </summary>
public sealed record StatsAccessCaseFixture(
    CallerKindFixture Caller,
    long CallerUserId,
    bool TargetResolves,
    StatsChannelFixture? Channel,
    StatsChannelKindFixture RequiredKind,
    bool CheckJoinable,
    bool CallerIsMember,
    bool CallerIsAdmin)
{
    public override string ToString() =>
        $"AccessCase(caller={Caller}, targetResolves={TargetResolves}, requiredKind={RequiredKind}, " +
        $"checkJoinable={CheckJoinable}, member={CallerIsMember}, admin={CallerIsAdmin}, channel={Channel})";
}

/// <summary>A single per-day metric value; <see cref="UtcDay"/> is a Unix-second timestamp at 00:00:00 UTC.</summary>
public readonly record struct DailyMetricPointFixture(int UtcDay, long Value)
{
    public override string ToString() => $"(day={UtcDay}, value={Value})";
}

/// <summary>
/// A sparse per-day metric series for one metric. Days are unique and ascending; gaps between recorded
/// days exercise the Metrics_Store zero-fill behaviour (Requirements 10.5, 10.6). The optional
/// <see cref="ReportingWindowDays"/> feeds reporting-period computation (Requirement 10.3).
/// </summary>
public sealed record DailyMetricSeriesFixture(
    string Metric,
    IReadOnlyList<DailyMetricPointFixture> Points,
    int ReportingWindowDays)
{
    public override string ToString() =>
        $"Series('{Metric}', points={Points.Count}, window={ReportingWindowDays}d)";
}

/// <summary>One data-series column of a statistics graph.</summary>
public sealed record GraphSeriesFixture(
    string Id,
    string Name,
    string ColorKey,
    IReadOnlyList<long> Values)
{
    public override string ToString() => $"Series('{Id}':'{Name}', color='{ColorKey}', n={Values.Count})";
}

/// <summary>
/// A statistics-graph specification: an ascending Unix-millisecond x axis plus one or more data series
/// (each with the same value count as the x axis), and an optional zoomed spec that drives zoom-token
/// generation. Empty (zero x points, zero series values) and multi-series cases are all producible so
/// Properties 10/11/12 can cover them.
/// </summary>
public sealed record GraphSpecFixture(
    IReadOnlyList<long> XAxisMillis,
    IReadOnlyList<GraphSeriesFixture> Series,
    bool Dark,
    GraphSpecFixture? Zoom = null)
{
    public bool IsEmpty => XAxisMillis.Count == 0;
    public bool HasZoom => Zoom is not null;

    public override string ToString() =>
        $"GraphSpec(x={XAxisMillis.Count}, series={Series.Count}, dark={Dark}, zoom={HasZoom})";
}

/// <summary>The two kinds of statistics source that can be publicly forwarded.</summary>
public enum ForwardSourceTypeFixture
{
    Message,
    Story
}

/// <summary>A single public-forward event in a generated sequence.</summary>
public enum ForwardOpFixture
{
    Record,
    Remove
}

/// <summary>
/// One forward event against a source. <see cref="ForwardingPeerIsPublic"/> marks whether the forwarding
/// chat/channel has a public username (only public forwarders are recorded, Requirement 11.5). Repeated
/// <c>(SourceItemId, ForwardingPeerId, ForwardingMsgId)</c> tuples and interleaved
/// <see cref="ForwardOpFixture.Remove"/> ops let Property 17 exercise dedup/removal.
/// </summary>
public sealed record ForwardEventFixture(
    ForwardOpFixture Op,
    ForwardSourceTypeFixture SourceType,
    long SourceOwnerPeerId,
    long SourceItemId,
    long ForwardingPeerId,
    int ForwardingMsgId,
    bool ForwardingPeerIsPublic,
    long OrderKey)
{
    public override string ToString() =>
        $"{Op}({SourceType} {SourceOwnerPeerId}/{SourceItemId} <- {ForwardingPeerId}/{ForwardingMsgId}, " +
        $"public={ForwardingPeerIsPublic}, order={OrderKey})";
}

/// <summary>A generated sequence of forward events for one source, plus the requested page size.</summary>
public sealed record ForwardEventSequenceFixture(
    ForwardSourceTypeFixture SourceType,
    long SourceOwnerPeerId,
    long SourceItemId,
    IReadOnlyList<ForwardEventFixture> Events,
    int Limit)
{
    public override string ToString() =>
        $"ForwardSeq({SourceType} {SourceOwnerPeerId}/{SourceItemId}, events={Events.Count}, limit={Limit})";
}

/// <summary>
/// An async-graph token fixture. The three independent condition toggles (unrecognized / expired /
/// outdated) and the zoom-availability flag let Properties 12/13/14 exercise token round-trip, the
/// unrecognized-token rejection, and the fixed error precedence
/// (recognition -> expiry -> currency -> zoom). <see cref="IssuedAt"/> and <see cref="NowUnix"/> are
/// Unix-second timestamps; the validity window is 86,400 seconds.
/// </summary>
public sealed record AsyncTokenFixture(
    string Token,
    bool IsRecognized,
    bool IsExpired,
    bool IsOutdated,
    int IssuedAt,
    int NowUnix,
    GraphSpecFixture Spec,
    GraphSpecFixture? ZoomSpec,
    long? ZoomX)
{
    public const int ValidityWindowSeconds = 86_400;

    public bool HasZoom => ZoomSpec is not null;

    public override string ToString() =>
        $"AsyncToken('{Token}', recognized={IsRecognized}, expired={IsExpired}, outdated={IsOutdated}, " +
        $"zoom={HasZoom}, zoomX={ZoomX})";
}
