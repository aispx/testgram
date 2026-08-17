namespace MyTelegram.Converters.Mappers.LatestLayer;

internal sealed class ChatInviteExportedMapper
    : IObjectMapper<IChatInviteReadModel, TChatInviteExported>,
        ILayeredMapper,
        ITransientDependency
{
    public int Layer => Layers.LayerLatest;

    internal static TStarsSubscriptionPricing? ToSubscriptionPricing(int? period, long? amount)
    {
        return period is > 0 && amount is > 0
            ? new TStarsSubscriptionPricing { Period = period.Value, Amount = amount.Value }
            : null;
    }


    public TChatInviteExported Map(IChatInviteReadModel source)
    {
        return Map(source, new TChatInviteExported());
    }

    public TChatInviteExported Map(
        IChatInviteReadModel source,
        TChatInviteExported destination
    )
    {
        destination.Revoked = source.Revoked;
        destination.Permanent = source.Permanent;
        destination.RequestNeeded = source.RequestNeeded;
        destination.Link = source.Link;
        destination.AdminId = source.AdminId;
        destination.Date = source.Date;
        destination.StartDate = source.StartDate;
        destination.ExpireDate = source.ExpireDate;
        destination.UsageLimit = source.UsageLimit;
        destination.Usage = source.Usage;
        destination.Requested = source.Requested;
        destination.Title = source.Title;
        destination.SubscriptionPricing = ToSubscriptionPricing(source.SubscriptionPricingPeriod, source.SubscriptionPricingAmount);
        // SubscriptionExpired is derived from the importer read model, so the caller fills it in.

        return destination;
    }
}