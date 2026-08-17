using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarsSubscriptions;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

/// <summary>
/// List the user's Telegram Star subscriptions to channels, bought through paid invite links.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.getStarsSubscriptions"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetStarsSubscriptionsHandler(
    IMongoDatabase mongoDatabase,
    IStarsSubscriptionService starsSubscriptionService,
    IChatConverterService chatConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetStarsSubscriptions, MyTelegram.Schema.Payments.IStarsStatus>
{
    protected override async Task<MyTelegram.Schema.Payments.IStarsStatus> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetStarsSubscriptions obj)
    {
        var balance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);
        var documents = await starsSubscriptionService.GetSubscriptionsAsync(input.UserId);
        var now = CurrentDate;

        // missing_balance asks only for subscriptions that are due for renewal but cannot be paid
        // for out of the current balance.
        if (obj.MissingBalance)
        {
            documents = documents.Where(p => !p.Canceled && p.UntilDate <= now && p.Amount > balance).ToList();
        }

        var chats = new List<IChat>();
        var subscriptions = new List<IStarsSubscription>();
        foreach (var document in documents)
        {
            chats.Add(await chatConverterService.GetChannelAsync(input, document.PeerId, false, false, input.Layer));

            subscriptions.Add(new TStarsSubscription
            {
                Id = document.Id,
                Peer = document.PeerId.ToChannelPeer().ToPeer(),
                UntilDate = document.UntilDate,
                Pricing = new TStarsSubscriptionPricing { Period = document.Period, Amount = document.Amount },
                ChatInviteHash = document.InviteHash,
                Canceled = document.Canceled,
                BotCanceled = document.BotCanceled,
                // The channel can be re-joined without paying again while the paid period runs.
                CanRefulfill = document.UntilDate > now,
                MissingBalance = !document.Canceled && document.UntilDate <= now && document.Amount > balance
            });
        }

        return new TStarsStatus
        {
            Balance = new TStarsAmount { Amount = balance },
            Subscriptions = [.. subscriptions],
            Chats = [.. chats],
            Users = new TVector<IUser>()
        };
    }
}
