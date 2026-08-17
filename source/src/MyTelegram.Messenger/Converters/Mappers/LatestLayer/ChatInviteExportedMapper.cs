// ReSharper disable All

namespace MyTelegram.Messenger.Converters.Mappers.LatestLayer;

internal sealed class ChatInviteExportedMapper
    : IObjectMapper<ChatInviteCreatedEvent, TChatInviteExported>,
        IObjectMapper<ChatInviteEditedEvent, TChatInviteExported>,

ILayeredMapper,
        ITransientDependency
{
    public int Layer => Layers.LayerLatest;
    public int RequestLayer { get; set; }

    public TChatInviteExported Map(ChatInviteCreatedEvent source)
    {
        return Map(source, new TChatInviteExported());
    }

    public TChatInviteExported Map(
        ChatInviteCreatedEvent source,
        TChatInviteExported destination
    )
    {
        destination.Revoked = false;
        destination.Permanent = source.Permanent;
        destination.RequestNeeded = source.RequestNeeded;
        //destination.Link = source.Link;
        destination.AdminId = source.AdminId;
        destination.Date = source.Date;
        destination.StartDate = source.StartDate;
        destination.ExpireDate = source.ExpireDate;
        destination.UsageLimit = source.UsageLimit;
        destination.Usage = 0;
        destination.Title = source.Title;
        destination.SubscriptionPricing = ToSubscriptionPricing(source.SubscriptionPricingPeriod, source.SubscriptionPricingAmount);

        return destination;
    }

    [return: NotNullIfNotNull("source")]
    public TChatInviteExported? Map(ChatInviteEditedEvent source)
    {
        return Map(source, new TChatInviteExported());
    }

    [return: NotNullIfNotNull("source")]
    public TChatInviteExported? Map(ChatInviteEditedEvent source, TChatInviteExported destination)
    {
        destination.Revoked = source.Revoked;
        destination.Permanent = source.Permanent;
        destination.RequestNeeded = source.RequestNeeded;
        //destination.Link = source.Link;
        destination.AdminId = source.AdminId;
        destination.Date = source.Date;
        destination.StartDate = source.StartDate;
        destination.ExpireDate = source.ExpireDate;
        destination.UsageLimit = source.UsageLimit;
        destination.Usage = source.Usage;
        destination.Requested = source.Requested;
        destination.Title = source.Title;
        destination.SubscriptionPricing = ToSubscriptionPricing(source.SubscriptionPricingPeriod, source.SubscriptionPricingAmount);

        return destination;
    }

    private static TStarsSubscriptionPricing? ToSubscriptionPricing(int? period, long? amount)
    {
        return period is > 0 && amount is > 0
            ? new TStarsSubscriptionPricing { Period = period.Value, Amount = amount.Value }
            : null;
    }
}
