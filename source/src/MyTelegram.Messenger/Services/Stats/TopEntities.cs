namespace MyTelegram.Messenger.Services.Stats;

/// <summary>
/// An active poster in a supergroup over the Period (maps to <c>statsGroupTopPoster</c>).
/// </summary>
public readonly record struct TopPoster(long UserId, int Messages, int AvgChars);

/// <summary>
/// An active admin in a supergroup over the Period (maps to <c>statsGroupTopAdmin</c>).
/// </summary>
public readonly record struct TopAdmin(long UserId, int Deleted, int Kicked, int Banned);

/// <summary>
/// An active inviter in a supergroup over the Period (maps to <c>statsGroupTopInviter</c>).
/// </summary>
public readonly record struct TopInviter(long UserId, int Invitations);

/// <summary>
/// The bounded, activity-sorted top-entity lists for a supergroup together with the
/// distinct user ids referenced across all three lists (used to populate <c>users</c>).
/// </summary>
/// <param name="Posters">Top posters, at most 10, ordered by message count descending.</param>
/// <param name="Admins">Top admins, at most 10, ordered by admin action count descending.</param>
/// <param name="Inviters">Top inviters, at most 10, ordered by invitation count descending.</param>
/// <param name="UserIds">Each distinct user id referenced by the three lists, exactly once.</param>
public sealed record TopEntities(
    IReadOnlyList<TopPoster> Posters,
    IReadOnlyList<TopAdmin> Admins,
    IReadOnlyList<TopInviter> Inviters,
    IReadOnlyList<long> UserIds);
