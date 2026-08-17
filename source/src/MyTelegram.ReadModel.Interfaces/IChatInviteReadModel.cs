namespace MyTelegram.ReadModel.Interfaces;

public interface IChatInviteReadModel : IReadModel
{
    string Id { get; }
    long InviteId { get; }
    long AdminId { get; }
    //long ChannelId { get; }
    long PeerId { get; }
    string? Title { get; }
    bool RequestNeeded { get; }
    int Date { get; }
    int? ExpireDate { get; }
    string Link { get; set; }
    bool Permanent { get; }
    bool Revoked { get; }
    int? StartDate { get; }
    int? Usage { get; }
    int? UsageLimit { get; }
    int? Requested { get; }
    bool IsBroadcast { get; }

    /// <summary>
    /// Period in seconds of the <a href="https://corefork.telegram.org/api/stars#star-subscriptions">Telegram Star subscription</a>
    /// this link sells, or null for a free invite link.
    /// </summary>
    int? SubscriptionPricingPeriod { get; }

    /// <summary>
    /// Amount of Telegram Stars charged per <see cref="SubscriptionPricingPeriod"/>.
    /// </summary>
    long? SubscriptionPricingAmount { get; }
}