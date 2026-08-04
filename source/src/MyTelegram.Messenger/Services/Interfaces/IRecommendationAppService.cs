namespace MyTelegram.Messenger.Services.Interfaces;

/// <summary>
/// Computes "similar channels"/"similar bots" recommendations from subscriber-base overlap.
/// See https://corefork.telegram.org/api/recommend
/// </summary>
public interface IRecommendationAppService
{
    /// <summary>
    /// Channels whose subscriber base overlaps with <paramref name="sourceChannelId"/>, ordered by
    /// the number of shared subscribers. When <paramref name="sourceChannelId"/> is null the source
    /// audience is taken from the channels <paramref name="selfUserId"/> has already joined.
    /// Channels the caller already joined are never returned.
    /// </summary>
    /// <param name="max">How many ids to return — the caller's own (premium-dependent) limit.</param>
    /// <param name="totalCap">
    /// Upper bound for the reported total. Clients render <c>count - list.size()</c> as "unlock N more
    /// with Premium", so the total must never exceed what a Premium account would actually receive.
    /// </param>
    /// <returns>At most <paramref name="max"/> channel ids, plus the total number of matches found.</returns>
    Task<RecommendationResult> GetSimilarChannelIdsAsync(long selfUserId, long? sourceChannelId, int max, int totalCap);

    /// <summary>
    /// Bots whose user base overlaps with <paramref name="botUserId"/>, ordered by the number of
    /// shared users.
    /// </summary>
    /// <param name="max">How many ids to return — the caller's own (premium-dependent) limit.</param>
    /// <param name="totalCap">Upper bound for the reported total, see the channel overload.</param>
    /// <returns>At most <paramref name="max"/> bot user ids, plus the total number of matches found.</returns>
    Task<RecommendationResult> GetSimilarBotIdsAsync(long selfUserId, long botUserId, int max, int totalCap);
}

public record RecommendationResult(List<long> Ids, int TotalCount)
{
    public static readonly RecommendationResult Empty = new([], 0);
}

/// <summary>
/// Cached candidate list for one recommendation source, held before the caller-specific filtering
/// (already-joined channels) and before truncation to the caller's limit, so a single entry serves
/// both premium and non-premium callers.
/// </summary>
public class RecommendationCacheItem
{
    public List<long> Ids { get; set; } = [];
}
