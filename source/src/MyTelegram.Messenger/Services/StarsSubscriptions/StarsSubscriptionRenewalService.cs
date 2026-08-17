using MongoDB.Driver;
using MyTelegram.Messenger.Handlers.LatestLayer.Payments;
using MyTelegram.Messenger.Services.Localization;

namespace MyTelegram.Messenger.Services.StarsSubscriptions;

public interface IStarsSubscriptionRenewalService
{
    /// <summary>
    /// Runs one renewal pass: warns about subscriptions that are about to renew without enough
    /// stars on the balance, and charges the ones whose period has run out. Returns how many
    /// subscriptions were looked at.
    /// </summary>
    Task<int> ProcessDueSubscriptionsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Auto-renewal of Telegram Star subscriptions to channels, bought through paid invite links, plus
/// the service notifications the user gets about it.
/// See https://corefork.telegram.org/api/invites#paid-invite-links
/// </summary>
public class StarsSubscriptionRenewalService(
    IMongoDatabase database,
    IStarsSubscriptionService starsSubscriptionService,
    IMessageAppService messageAppService,
    IUserLanguageResolver userLanguageResolver,
    IQueryProcessor queryProcessor,
    ICommandBus commandBus,
    ILogger<StarsSubscriptionRenewalService> logger) : IStarsSubscriptionRenewalService, ITransientDependency
{
    /// <summary>
    /// How long before a renewal the user is told that their balance will not cover it.
    /// </summary>
    public const int WarningLeadTime = 5 * 60;

    /// <summary>
    /// How long after the period ran out renewal keeps being retried, so a user who tops up late
    /// still keeps the subscription instead of having to buy it again.
    /// </summary>
    public const int RenewalRetryWindow = 3 * 24 * 60 * 60;

    /// <summary>How long a worker holds the right to charge one period.</summary>
    private const int RenewalLease = 60;

    private const int BatchSize = 200;

    /// <summary>Periods the "next 12 months" top-up button pays for.</summary>
    private const int AdvancePeriods = 12;

    public async Task<int> ProcessDueSubscriptionsAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow.ToTimestamp();
        var subscriptions = await starsSubscriptionService.GetRenewableAsync(now + WarningLeadTime, BatchSize);

        foreach (var subscription in subscriptions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (subscription.UntilDate > now)
                {
                    await WarnIfBalanceTooLowAsync(subscription);
                }
                else
                {
                    await RenewAsync(subscription, now);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process star subscription {SubscriptionId}", subscription.Id);
            }
        }

        return subscriptions.Count;
    }

    /// <summary>
    /// The warning is sent once per period: <c>LowBalanceWarningUntilDate</c> tracks the period it
    /// was sent for, and a successful charge resets it together with the rest of the document.
    /// </summary>
    private async Task WarnIfBalanceTooLowAsync(StarsSubscriptionDocument subscription)
    {
        if (subscription.LowBalanceWarningUntilDate == subscription.UntilDate)
        {
            return;
        }

        var balance = await StarsBalanceHelper.GetBalanceAsync(database, subscription.UserId);
        if (balance >= subscription.Amount)
        {
            return;
        }

        var channel = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(subscription.PeerId));
        if (channel == null)
        {
            return;
        }

        var language = await userLanguageResolver.GetLanguageAsync(subscription.UserId);
        var message = ServerTexts.StarsSubscriptionLowBalance(language,
            channel.Title,
            subscription.UntilDate,
            balance,
            subscription.Amount);

        await SendServiceMessageAsync(subscription.UserId, message, BuildTopupMarkup(language, subscription));
        await starsSubscriptionService.MarkLowBalanceWarningSentAsync(subscription.Id, subscription.UntilDate);

        logger.LogInformation(
            "Warned user {UserId} about a star subscription to channel {ChannelId} renewing at {UntilDate}",
            subscription.UserId, subscription.PeerId, subscription.UntilDate);
    }

    private async Task RenewAsync(StarsSubscriptionDocument subscription, int now)
    {
        if (now - subscription.UntilDate > RenewalRetryWindow)
        {
            return;
        }

        var channel = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(subscription.PeerId));
        if (channel == null)
        {
            return;
        }

        if (!await starsSubscriptionService.TryClaimRenewalAsync(subscription.Id, subscription.UntilDate, now,
                RenewalLease))
        {
            return;
        }

        var renewed = await starsSubscriptionService.ChargeAsync(subscription.UserId,
            subscription.PeerId,
            subscription.InviteHash,
            subscription.Period,
            subscription.Amount,
            channel.Title);

        var language = await userLanguageResolver.GetLanguageAsync(subscription.UserId);

        if (renewed == null)
        {
            // The balance is still too low. The subscription is left as it is, so it renews by
            // itself once the user tops up within the retry window, and the failure is reported
            // only once per period.
            if (subscription.RenewalFailedUntilDate != subscription.UntilDate)
            {
                await SendServiceMessageAsync(subscription.UserId,
                    ServerTexts.StarsSubscriptionExtendFailed(language, channel.Title, subscription.Amount),
                    BuildTopupMarkup(language, subscription));
                await starsSubscriptionService.MarkRenewalFailureReportedAsync(subscription.Id, subscription.UntilDate);
            }

            logger.LogInformation(
                "Star subscription {SubscriptionId} could not be renewed: balance below {Amount}",
                subscription.Id, subscription.Amount);

            return;
        }

        await ExtendMembershipAsync(subscription.UserId, subscription.PeerId, renewed.UntilDate);

        await SendServiceMessageAsync(subscription.UserId,
            ServerTexts.StarsSubscriptionExtended(language, channel.Title, subscription.Amount, renewed.UntilDate),
            replyMarkup: null);

        logger.LogInformation(
            "Renewed star subscription {SubscriptionId} for {Amount} stars, next renewal at {UntilDate}",
            subscription.Id, subscription.Amount, renewed.UntilDate);
    }

    /// <summary>
    /// Keeps <c>channel.subscription_until_date</c> in step with the subscription. A user who has
    /// left the channel keeps the subscription (they can re-join through
    /// <c>payments.fulfillStarsSubscription</c>), so there is nothing to update for them.
    /// </summary>
    private async Task ExtendMembershipAsync(long userId, long channelId, int untilDate)
    {
        var member = await queryProcessor.ProcessAsync(new GetChannelMemberByUserIdQuery(channelId, userId));
        if (member is not { Left: false, Kicked: false })
        {
            return;
        }

        var command = new ExtendChannelMemberSubscriptionCommand(
            ChannelMemberId.Create(channelId, userId),
            BuildRequestInfo(userId),
            channelId,
            userId,
            untilDate);
        await commandBus.PublishAsync(command);
    }

    private Task SendServiceMessageAsync(long userId, string message, IReplyMarkup? replyMarkup)
    {
        var sendInput = new SendMessageInput(
            BuildRequestInfo(MyTelegramConsts.NotificationServiceUserId),
            MyTelegramConsts.NotificationServiceUserId,
            new Peer(PeerType.User, userId),
            message,
            Random.Shared.NextInt64(),
            replyMarkup: replyMarkup);

        return messageAppService.SendMessageAsync([sendInput]);
    }

    /// <summary>
    /// Star top-up deep links, as understood by the official clients: the client opens the top-up
    /// form for at least <c>balance</c> stars and labels it with <c>purpose</c>.
    /// See https://corefork.telegram.org/api/links#stars-topup-links
    /// </summary>
    public static IReplyMarkup BuildTopupMarkup(string language, StarsSubscriptionDocument subscription)
    {
        var rows = new TVector<IKeyboardButtonRow>
        {
            new TKeyboardButtonRow
            {
                Buttons = new TVector<IKeyboardButton>(new TKeyboardButtonUrl
                {
                    Text = ServerTexts.StarsTopupButton(language),
                    Url = $"tg://stars_topup?balance={subscription.Amount}&purpose=subs"
                })
            }
        };

        // Paying a year ahead only makes sense for the monthly period the paid links use; any other
        // period has no "12 months" price to offer.
        if (subscription.Period == ChatInviteSubscriptionPricing.MonthlyPeriod)
        {
            rows.Add(new TKeyboardButtonRow
            {
                Buttons = new TVector<IKeyboardButton>(new TKeyboardButtonUrl
                {
                    Text = ServerTexts.StarsTopupYearButton(language),
                    Url =
                        $"tg://stars_topup?balance={subscription.Amount * AdvancePeriods}&purpose=subadvance12"
                })
            });
        }

        return new TReplyInlineMarkup { Rows = rows };
    }

    private static RequestInfo BuildRequestInfo(long userId) => RequestInfo.Empty with
    {
        UserId = userId,
        Layer = MyTelegramConsts.Layer,
        Date = DateTime.UtcNow.ToTimestamp(),
        RequestId = Guid.NewGuid(),
        DeviceType = DeviceType.Android
    };
}
