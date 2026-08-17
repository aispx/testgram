namespace MyTelegram.Messenger.Services;

/// <summary>
/// Validated pricing of a paid (Telegram Star subscription) invite link.
/// See https://corefork.telegram.org/api/invites#paid-invite-links
/// </summary>
public sealed record ChatInviteSubscriptionPricing(int Period, long Amount)
{
    /// <summary>
    /// The only subscription period production Telegram accepts: 30 days.
    /// </summary>
    public const int MonthlyPeriod = 2592000;

    /// <summary>
    /// Upper bound on the per-period price, mirroring the <c>stars_subscription_amount_max</c>
    /// config key published by <see cref="Impl.AppConfigHelper"/>.
    /// </summary>
    public const long MaxAmount = 10000;

    /// <summary>
    /// Validates the pricing supplied by the client and returns null when the link is free.
    /// </summary>
    /// <param name="pricing">Pricing from the request, if any.</param>
    /// <param name="broadcast">Whether the target peer is a broadcast channel.</param>
    /// <param name="requestNeeded">Whether the link also asks for admin approval.</param>
    /// <param name="usageLimit">Usage limit requested for the link, if any.</param>
    public static ChatInviteSubscriptionPricing? Validate(IStarsSubscriptionPricing? pricing,
        bool broadcast,
        bool requestNeeded,
        int? usageLimit)
    {
        if (pricing is not TStarsSubscriptionPricing p)
        {
            return null;
        }

        if (p.Period != MonthlyPeriod)
        {
            RpcErrors.RpcErrors400.SubscriptionPeriodInvalid.ThrowRpcError();
        }

        // Only broadcast channels can be subscribed to, and a paid link can neither gate on admin
        // approval nor cap the number of subscribers.
        if (!broadcast || requestNeeded || usageLimit is > 0 || p.Amount <= 0 || p.Amount > MaxAmount)
        {
            RpcErrors.RpcErrors400.PricingChatInvalid.ThrowRpcError();
        }

        return new ChatInviteSubscriptionPricing(p.Period, p.Amount);
    }
}
