namespace MyTelegram.ReadModel.Impl;

public class ChatInviteImporterReadModel : IChatInviteImporterReadModel,
        IAmReadModelFor<ChatInviteAggregate, ChatInviteId, ChatInviteImportedEvent>,
        IAmReadModelFor<JoinChannelAggregate, JoinChannelId, JoinChannelRequestUpdatedEvent>
{
    public string Id { get; private set; } = default!;
    public long PeerId { get; private set; }
    public long InviteId { get; private set; }
    public long UserId { get; private set; }
    public ChatInviteRequestState ChatInviteRequestState { get; private set; }
    //public bool RequestNeeded { get; private set; }
    public bool Approved { get; private set; }
    public long? ApprovedBy { get; private set; }
    public int Date { get; private set; }
    public string? About { get; private set; }
    public bool ViaChatList { get; private set; }
    public int? SubscriptionUntilDate { get; private set; }
    public long? Version { get; set; }

    public Task ApplyAsync(IReadModelContext context, IDomainEvent<ChatInviteAggregate, ChatInviteId, ChatInviteImportedEvent> domainEvent, CancellationToken cancellationToken)
    {
        Id = ChatInviteImporterId.Create(domainEvent.AggregateEvent.ChannelId, domainEvent.AggregateEvent.RequestInfo.UserId).Value;
        PeerId = domainEvent.AggregateEvent.ChannelId;
        InviteId = domainEvent.AggregateEvent.InviteId;
        UserId = domainEvent.AggregateEvent.RequestInfo.UserId;
        ChatInviteRequestState = domainEvent.AggregateEvent.ChatInviteRequestState;
        Date = domainEvent.AggregateEvent.Date;
        SubscriptionUntilDate = domainEvent.AggregateEvent.SubscriptionUntilDate;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context, IDomainEvent<JoinChannelAggregate, JoinChannelId, JoinChannelRequestUpdatedEvent> domainEvent, CancellationToken cancellationToken)
    {
        // Join requests can also come from channels.joinChannel, without any invite link. Those
        // have no importer record, and must not create one - they never imported a link.
        if (context.IsNew)
        {
            context.MarkForDeletion();

            return Task.CompletedTask;
        }

        Approved = domainEvent.AggregateEvent.Approved;
        ApprovedBy = domainEvent.AggregateEvent.RequestInfo.UserId;
        ChatInviteRequestState = domainEvent.AggregateEvent.Approved
            ? ChatInviteRequestState.Approved
            : ChatInviteRequestState.Rejected;

        return Task.CompletedTask;
    }
}
