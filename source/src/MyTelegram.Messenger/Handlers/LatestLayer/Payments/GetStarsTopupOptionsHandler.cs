namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class GetStarsTopupOptionsHandler
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetStarsTopupOptions, TVector<MyTelegram.Schema.IStarsTopupOption>>
{
    protected override Task<TVector<MyTelegram.Schema.IStarsTopupOption>> HandleCoreAsync(
        IRequestInput input, MyTelegram.Schema.Payments.RequestGetStarsTopupOptions obj)
    {
        var options = StripeHelper.TopupOptions.Select((o, i) => (IStarsTopupOption)new TStarsTopupOption
        {
            Stars = o.Stars,
            StoreProduct = o.StoreProduct,
            Currency = "USD",
            Amount = o.Amount,
            Extended = i == StripeHelper.TopupOptions.Length - 1, // last option is "extended"
        }).ToList();

        return Task.FromResult(new TVector<IStarsTopupOption>(options));
    }
}
