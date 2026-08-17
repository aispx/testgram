namespace MyTelegram.Domain.Aggregates.ChatInvite;

public class ChatInviteState : AggregateState<ChatInviteAggregate, ChatInviteId, ChatInviteState>,
    IApply<ChatInviteCreatedEvent>,
    IApply<ChatInviteEditedEvent>,
    IApply<ChatInviteImportedEvent>,
    IApply<ChatInviteDeletedEvent>,
    IApply<ChatInviteExportedEvent>,
    IApply<ChatInviteReovkedEvent>
{
    public long ChannelId { get; private set; }
    public long InviteId { get; private set; }
    public string Hash { get; private set; } = default!;
    public long AdminId { get; private set; }
    public string? Title { get; private set; }
    public bool RequestNeeded { get; private set; }
    public int Date { get; private set; }
    public int? StartDate { get; private set; }
    public int? ExpireDate { get; private set; }
    public int? UsageLimit { get; private set; }
    public bool Permanent { get; private set; }
    public bool Revoked { get; private set; }

    public int? Requested { get; private set; }
    public int? Usage { get; private set; }
    public bool IsBroadcast { get; private set; }

    /// <summary>
    /// Period in seconds of the <a href="https://corefork.telegram.org/api/stars#star-subscriptions">Telegram Star subscription</a>
    /// this link sells, or null for a free invite link.
    /// </summary>
    public int? SubscriptionPricingPeriod { get; private set; }

    /// <summary>
    /// Amount of Telegram Stars charged per <see cref="SubscriptionPricingPeriod"/>.
    /// </summary>
    public long? SubscriptionPricingAmount { get; private set; }

    public void Apply(ChatInviteCreatedEvent aggregateEvent)
    {
        ChannelId = aggregateEvent.ChannelId;
        InviteId = aggregateEvent.InviteId;
        Hash = aggregateEvent.Hash;
        AdminId = aggregateEvent.AdminId;
        Title = aggregateEvent.Title;
        RequestNeeded = aggregateEvent.RequestNeeded;
        StartDate = aggregateEvent.StartDate;
        ExpireDate = aggregateEvent.ExpireDate;
        UsageLimit = aggregateEvent.UsageLimit;
        Permanent = aggregateEvent.Permanent;
        IsBroadcast = aggregateEvent.IsBroadcast;
        Date = aggregateEvent.Date;
        SubscriptionPricingPeriod = aggregateEvent.SubscriptionPricingPeriod;
        SubscriptionPricingAmount = aggregateEvent.SubscriptionPricingAmount;
    }

    public void Apply(ChatInviteEditedEvent aggregateEvent)
    {
        Revoked = aggregateEvent.Revoked;
        Hash = aggregateEvent.Hash;
        Title = aggregateEvent.Title;
        RequestNeeded = aggregateEvent.RequestNeeded;
        StartDate = aggregateEvent.StartDate;
        ExpireDate = aggregateEvent.ExpireDate;
        UsageLimit = aggregateEvent.UsageLimit;
        SubscriptionPricingPeriod = aggregateEvent.SubscriptionPricingPeriod;
        SubscriptionPricingAmount = aggregateEvent.SubscriptionPricingAmount;
    }

    public void LoadSnapshot(ChatInviteSnapshot snapshot)
    {
        ChannelId = snapshot.ChannelId;
        InviteId = snapshot.InviteId;
        Hash = snapshot.Hash;
        AdminId = snapshot.AdminId;
        Title = snapshot.Title;
        RequestNeeded = snapshot.RequestNeeded;
        Date = snapshot.Date;
        StartDate = snapshot.StartDate;
        ExpireDate = snapshot.ExpireDate;
        UsageLimit = snapshot.UsageLimit;
        Permanent = snapshot.Permanent;
        Usage = snapshot.Usage;
        Requested = snapshot.Requested;
        IsBroadcast = snapshot.IsBroadcast;
        SubscriptionPricingPeriod = snapshot.SubscriptionPricingPeriod;
        SubscriptionPricingAmount = snapshot.SubscriptionPricingAmount;
    }

    public void Apply(ChatInviteImportedEvent aggregateEvent)
    {
        Usage = aggregateEvent.Usage;
        Requested = aggregateEvent.Requested;
    }

    public void Apply(ChatInviteDeletedEvent aggregateEvent)
    {
        //throw new NotImplementedException();
    }

    public void Apply(ChatInviteExportedEvent aggregateEvent)
    {
        ChannelId = aggregateEvent.ChannelId;
        InviteId = aggregateEvent.InviteId;
        Hash = aggregateEvent.Hash;
        AdminId = aggregateEvent.AdminId;
        Title = aggregateEvent.Title;
        RequestNeeded = aggregateEvent.RequestNeeded;
        StartDate = aggregateEvent.StartDate;
        ExpireDate = aggregateEvent.ExpireDate;
        UsageLimit = aggregateEvent.UsageLimit;
        Permanent = aggregateEvent.Permanent;
        IsBroadcast = aggregateEvent.IsBroadcast;
        Date = aggregateEvent.Date;
        SubscriptionPricingPeriod = aggregateEvent.SubscriptionPricingPeriod;
        SubscriptionPricingAmount = aggregateEvent.SubscriptionPricingAmount;
    }

    public void Apply(ChatInviteReovkedEvent aggregateEvent)
    {
        Revoked = true;
    }
}