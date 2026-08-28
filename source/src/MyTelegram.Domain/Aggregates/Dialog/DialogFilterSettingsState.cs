namespace MyTelegram.Domain.Aggregates.Dialog;

public class DialogFilterSettingsState : AggregateState<DialogFilterSettingsAggregate, DialogFilterSettingsId,
        DialogFilterSettingsState>,
    IApply<DialogFiltersOrderUpdatedEvent>,
    IApply<DialogFilterTagsToggledEvent>,
    IApply<DialogArchivePinnedUpdatedEvent>
{
    public long OwnerUserId { get; private set; }
    public List<int> Order { get; private set; } = [];
    public bool TagsEnabled { get; private set; }
    public bool ArchivePinned { get; private set; }

    public void Apply(DialogFiltersOrderUpdatedEvent aggregateEvent)
    {
        OwnerUserId = aggregateEvent.OwnerUserId;
        Order = [.. aggregateEvent.Order];
    }

    public void Apply(DialogFilterTagsToggledEvent aggregateEvent)
    {
        OwnerUserId = aggregateEvent.OwnerUserId;
        TagsEnabled = aggregateEvent.Enabled;
    }

    public void Apply(DialogArchivePinnedUpdatedEvent aggregateEvent)
    {
        OwnerUserId = aggregateEvent.OwnerUserId;
        ArchivePinned = aggregateEvent.Pinned;
    }
}
