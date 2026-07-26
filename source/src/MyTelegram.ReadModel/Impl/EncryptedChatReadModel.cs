namespace MyTelegram.ReadModel.Impl;

public partial class EncryptedChatReadModel : IEncryptedChatReadModel,
    IAmReadModelFor<EncryptedChatAggregate, EncryptedChatId, EncryptedChatCreatedEvent>,
    IAmReadModelFor<EncryptedChatAggregate, EncryptedChatId, EncryptedChatAcceptedEvent>,
    IAmReadModelFor<EncryptedChatAggregate, EncryptedChatId, EncryptedChatDiscardedEvent>,
    IAmReadModelFor<EncryptedChatAggregate, EncryptedChatId, EncryptedChatSpamReportedEvent>
{
    public long AccessHash { get; private set; }
    public long AdminPermAuthKeyId { get; private set; }
    public long AdminId { get; private set; }
    public long ChatId { get; private set; }
    public ChatState ChatState { get; private set; }
    public int Date { get; private set; }
    public byte[] Ga { get; private set; } = [];
    public byte[] Gb { get; private set; } = [];
    public bool HistoryDeleted { get; private set; }
    public string Id { get; private set; } = null!;
    public long KeyFingerprint { get; private set; }
    public long ParticipantPermAuthKeyId { get; private set; }
    public long ParticipantId { get; private set; }
    public long RandomId { get; private set; }
    public List<long> SpamReporters { get; private set; } = [];

    public long? Version { get; set; }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<EncryptedChatAggregate, EncryptedChatId, EncryptedChatCreatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        Id = domainEvent.AggregateIdentity.Value;
        ChatId = domainEvent.AggregateEvent.ChatId;
        AdminId = domainEvent.AggregateEvent.AdminId;
        ParticipantId = domainEvent.AggregateEvent.ParticipantId;
        AdminPermAuthKeyId = domainEvent.AggregateEvent.AdminPermAuthKeyId;
        AccessHash = domainEvent.AggregateEvent.AccessHash;
        Ga = domainEvent.AggregateEvent.Ga;
        RandomId = domainEvent.AggregateEvent.RandomId;
        Date = domainEvent.AggregateEvent.Date;
        ChatState = ChatState.Waiting;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<EncryptedChatAggregate, EncryptedChatId, EncryptedChatAcceptedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        ParticipantPermAuthKeyId = domainEvent.AggregateEvent.ParticipantPermAuthKeyId;
        Gb = domainEvent.AggregateEvent.Gb;
        KeyFingerprint = domainEvent.AggregateEvent.KeyFingerprint;
        ChatState = ChatState.Active;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<EncryptedChatAggregate, EncryptedChatId, EncryptedChatDiscardedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        // Do NOT MarkForDeletion: the row must survive so subsequent calls resolve the chat
        // and get ENCRYPTION_DECLINED / ENCRYPTION_ALREADY_DECLINED instead of ENCRYPTION_ID_INVALID.
        HistoryDeleted = domainEvent.AggregateEvent.DeleteHistory;
        ChatState = ChatState.Discarded;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<EncryptedChatAggregate, EncryptedChatId, EncryptedChatSpamReportedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var reporterId = domainEvent.AggregateEvent.ReporterId;
        if (!SpamReporters.Contains(reporterId))
        {
            SpamReporters = [.. SpamReporters, reporterId];
        }

        return Task.CompletedTask;
    }
}
