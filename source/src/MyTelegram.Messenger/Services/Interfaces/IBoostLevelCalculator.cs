namespace MyTelegram.Messenger.Services.Interfaces;

/// <summary>
/// Computes a channel's <a href="https://core.telegram.org/api/boost">boost level</a> from the
/// boosts stored in the <c>channel_boosts</c> collection. Used both by premium.getBoostsStatus
/// and by the boost gates of features such as peer colors (channels.updateColor).
/// </summary>
public interface IBoostLevelCalculator
{
    /// <summary>Total boosts of the channel, honouring each boost's multiplier.</summary>
    Task<int> GetTotalBoostsAsync(long channelId);

    /// <summary>Current boost level of the channel.</summary>
    Task<int> GetLevelAsync(long channelId);

    /// <summary>Boost level reached with the given number of boosts.</summary>
    int CalculateLevel(int boosts);

    /// <summary>Number of boosts required to reach the given level.</summary>
    int GetBoostsForLevel(int level);
}
