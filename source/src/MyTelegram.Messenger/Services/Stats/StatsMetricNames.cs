namespace MyTelegram.Messenger.Services.Stats;

/// <summary>
/// Canonical metric names recorded into the Metrics_Store, shared by the ingestion subscribers
/// (write path) and the Stats_Service (read path).
/// </summary>
public static class StatsMetricNames
{
    // --- Gauge-family metrics (absolute values; RecordAsync uses set-semantics) ---

    /// <summary>Absolute subscriber/follower count of a channel.</summary>
    public const string Followers = "followers";

    /// <summary>Absolute member count of a supergroup.</summary>
    public const string Members = "members";

    /// <summary>Absolute count of subscribers who have the channel muted.</summary>
    public const string Muted = "muted";

    /// <summary>Absolute count of subscribers with notifications enabled.</summary>
    public const string NotifyOn = "notify_on";

    // --- Counter-family metrics (accumulated; RecordAsync uses $inc) ---

    /// <summary>Message/post view count.</summary>
    public const string Views = "views";

    /// <summary>Message/post share (forward) count.</summary>
    public const string Shares = "shares";

    /// <summary>Reaction count.</summary>
    public const string Reactions = "reactions";

    /// <summary>Message post count (supergroup activity).</summary>
    public const string Messages = "messages";

    /// <summary>Distinct viewer count (supergroup activity).</summary>
    public const string Viewers = "viewers";

    /// <summary>Distinct poster count (supergroup activity).</summary>
    public const string Posters = "posters";

    /// <summary>Story view count (channel-level; per-story views use <see cref="Views"/> on the story entity).</summary>
    public const string StoryViews = "story_views";

    /// <summary>Story share/forward count (channel-level).</summary>
    public const string StoryShares = "story_shares";

    /// <summary>Story reaction count (channel-level).</summary>
    public const string StoryReactions = "story_reactions";

    /// <summary>Story publication count (channel-level; denominator of the per-story means).</summary>
    public const string StoryPosts = "story_posts";

    /// <summary>Join count with a breakdown keyed by join source (invite link, search, ...).</summary>
    public const string JoinsBySource = "joins_by_source";

    /// <summary>Join count with a breakdown keyed by the joining user's language code.</summary>
    public const string JoinsByLanguage = "joins_by_language";

    /// <summary>View count with a breakdown keyed by hour-of-day ("0".."23", UTC).</summary>
    public const string ViewsByHour = "views_by_hour";

    /// <summary>Message count with a breakdown keyed by hour-of-day ("0".."23", UTC).</summary>
    public const string MessagesByHour = "messages_by_hour";

    /// <summary>Message count with a breakdown keyed by weekday name ("Monday".."Sunday", UTC).</summary>
    public const string MessagesByWeekday = "messages_by_weekday";

    /// <summary>Group activity count: messages plus member joins and leaves (supergroup actions graph).</summary>
    public const string Actions = "actions";

    // --- Per-post metadata (for recent-post interactions) ---

    /// <summary>
    /// The post/story date (Unix seconds), recorded as an absolute gauge so recent-post interactions
    /// can be ordered newest-first.
    /// </summary>
    public const string PostDate = "post_date";

    // --- Top-entity metrics (breakdown keyed by user id) ---

    /// <summary>Top-poster message counts, breakdown keyed by user id.</summary>
    public const string TopPosterMessages = "top_poster_messages";

    /// <summary>Top-poster total character counts, breakdown keyed by user id.</summary>
    public const string TopPosterChars = "top_poster_chars";

    /// <summary>Top-admin deleted-message counts, breakdown keyed by user id.</summary>
    public const string TopAdminDeleted = "top_admin_deleted";

    /// <summary>Top-admin kicked-user counts, breakdown keyed by user id.</summary>
    public const string TopAdminKicked = "top_admin_kicked";

    /// <summary>Top-admin banned-user counts, breakdown keyed by user id.</summary>
    public const string TopAdminBanned = "top_admin_banned";

    /// <summary>Top-inviter invitation counts, breakdown keyed by user id.</summary>
    public const string TopInviterInvitations = "top_inviter_invitations";

    private static readonly HashSet<string> GaugeMetrics = new(StringComparer.Ordinal)
    {
        Followers,
        Members,
        Muted,
        NotifyOn,
        PostDate
    };

    /// <summary>
    /// Returns <see langword="true"/> when the metric belongs to the absolute-gauge family
    /// (recorded with set-semantics rather than accumulation).
    /// </summary>
    public static bool IsGauge(string metric) => GaugeMetrics.Contains(metric);
}
