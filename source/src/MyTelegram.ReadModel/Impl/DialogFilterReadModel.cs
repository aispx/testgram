namespace MyTelegram.ReadModel.Impl;

public class DialogFilterReadModel : IDialogFilterReadModel,
    IAmReadModelFor<DialogFilterAggregate,DialogFilterId,DialogFilterUpdatedEvent>,
    IAmReadModelFor<DialogFilterAggregate,DialogFilterId,DialogFilterDeletedEvent>
{
    public long OwnerUserId { get; private set; }
    public int FolderId { get; private set; }
    public bool IsShareableFolder { get; private set; }
    public DialogFilter Filter { get; private set; } = null!;
    public string? ImportedFromSlug { get; private set; }
    public virtual string Id { get; private set; } = null!;
    public virtual long? Version { get; set; }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<DialogFilterAggregate, DialogFilterId, DialogFilterUpdatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        Id = domainEvent.AggregateIdentity.Value;
        OwnerUserId = domainEvent.AggregateEvent.OwnerUserId;
        FolderId = domainEvent.AggregateEvent.Filter.Id;
        Filter = domainEvent.AggregateEvent.Filter;
        // A folder imported from a chat folder deep link must come back as dialogFilterChatlist, and the
        // slug is its identity: the exporter's filter id collides with the importer's own folders.
        IsShareableFolder = domainEvent.AggregateEvent.Filter.IsChatlist;
        ImportedFromSlug = domainEvent.AggregateEvent.Filter.ImportedFromSlug;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<DialogFilterAggregate, DialogFilterId, DialogFilterDeletedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        context.MarkForDeletion();

        return Task.CompletedTask;
    }
}
