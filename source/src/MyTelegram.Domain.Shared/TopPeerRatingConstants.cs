// ReSharper disable once CheckNamespace

namespace MyTelegram;

/// <summary>
/// Numbers shared between the <a href="https://corefork.telegram.org/api/top-rating">top peer
/// rating</a> engine and the <c>config</c> the server advertises.
/// </summary>
public static class TopPeerRatingConstants
{
    /// <summary>
    /// <c>config.rating_e_decay</c>. Clients compute their own rating increments with it
    /// (tdlib <c>TopDialogManager::rating_add</c> — <c>exp((used - rating_timestamp) / rating_e_decay)</c>,
    /// Android <c>MediaDataController.increasePeerRaiting</c> — <c>Math.exp(dt / ratingDecay)</c>) and add
    /// them to the numbers this server sent, so the server has to use the very same decay or the two
    /// halves of the rating end up on different scales and the client's local ordering drifts away from
    /// ours after the first message. tdlib also gates story display on the absolute value
    /// (<c>MIN_STORY_RATING = 10</c> for correspondents), so the magnitude is visible, not just the order.
    /// </summary>
    public const int RatingEDecaySeconds = 2419200;

    /// <summary>How far back message history is scanned when deriving a rating.</summary>
    public const int RatingWindowSeconds = 90 * 24 * 60 * 60;
}
