namespace MyTelegram.ReadModel.Impl;

public class DialogFilterSettingsReadModel : IDialogFilterSettingsReadModel,
    IAmReadModelFor<DialogFilterSettingsAggregate, DialogFilterSettingsId, DialogFiltersOrderUpdatedEvent>,
    IAmReadModelFor<DialogFilterSettingsAggregate, DialogFilterSettingsId, DialogFilterTagsToggledEvent>,
    IAmReadModelFor<DialogFilterSettingsAggregate, DialogFilterSettingsId, DialogArchivePinnedUpdatedEvent>
{
    public long OwnerUserId { get; private set; }
    public IReadOnlyList<int> Order { get; private set; } = [];
    public bool TagsEnabled { get; private set; }
    public bool ArchivePinned { get; private set; }
    public virtual string Id { get; private set; } = null!;
    public virtual long? Version { get; set; }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<DialogFilterSettingsAggregate, DialogFilterSettingsId, DialogFiltersOrderUpdatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        Id = domainEvent.AggregateIdentity.Value;
        OwnerUserId = domainEvent.AggregateEvent.OwnerUserId;
        Order = [.. domainEvent.AggregateEvent.Order];

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<DialogFilterSettingsAggregate, DialogFilterSettingsId, DialogFilterTagsToggledEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        Id = domainEvent.AggregateIdentity.Value;
        OwnerUserId = domainEvent.AggregateEvent.OwnerUserId;
        TagsEnabled = domainEvent.AggregateEvent.Enabled;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<DialogFilterSettingsAggregate, DialogFilterSettingsId, DialogArchivePinnedUpdatedEvent>
            domainEvent,
        CancellationToken cancellationToken)
    {
        Id = domainEvent.AggregateIdentity.Value;
        OwnerUserId = domainEvent.AggregateEvent.OwnerUserId;
        ArchivePinned = domainEvent.AggregateEvent.Pinned;

        return Task.CompletedTask;
    }
}
