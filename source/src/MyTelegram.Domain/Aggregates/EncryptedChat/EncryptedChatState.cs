namespace MyTelegram.Domain.Aggregates.EncryptedChat;

public class EncryptedChatState : AggregateState<EncryptedChatAggregate, EncryptedChatId, EncryptedChatState>,
    IApply<EncryptedChatCreatedEvent>,
    IApply<EncryptedChatAcceptedEvent>,
    IApply<EncryptedChatDiscardedEvent>,
    IApply<EncryptedChatSpamReportedEvent>
{
    public int ChatId { get; private set; }
    public long AccessHash { get; private set; }
    public long AdminId { get; private set; }
    public long ParticipantId { get; private set; }
    public long AdminPermAuthKeyId { get; private set; }
    public long ParticipantPermAuthKeyId { get; private set; }

    /// <summary>
    /// Opaque DH value g_a from the admin. Never inspected, never used for key computation.
    /// </summary>
    public byte[] Ga { get; private set; } = [];

    /// <summary>
    /// Opaque DH value g_b from the participant. Never inspected, never used for key computation.
    /// </summary>
    public byte[] Gb { get; private set; } = [];

    /// <summary>
    /// Opaque 64-bit key fingerprint supplied by the participant. The server cannot verify it.
    /// </summary>
    public long KeyFingerprint { get; private set; }

    public int RandomId { get; private set; }
    public int Date { get; private set; }
    public ChatState State { get; private set; } = ChatState.None;
    public bool HistoryDeleted { get; private set; }
    public HashSet<long> SpamReporters { get; private set; } = [];

    public void Apply(EncryptedChatCreatedEvent aggregateEvent)
    {
        ChatId = aggregateEvent.ChatId;
        AdminId = aggregateEvent.AdminId;
        ParticipantId = aggregateEvent.ParticipantId;
        AdminPermAuthKeyId = aggregateEvent.AdminPermAuthKeyId;
        AccessHash = aggregateEvent.AccessHash;
        Ga = aggregateEvent.Ga;
        RandomId = aggregateEvent.RandomId;
        Date = aggregateEvent.Date;
        State = ChatState.Waiting;
    }

    public void Apply(EncryptedChatAcceptedEvent aggregateEvent)
    {
        ParticipantPermAuthKeyId = aggregateEvent.ParticipantPermAuthKeyId;
        Gb = aggregateEvent.Gb;
        KeyFingerprint = aggregateEvent.KeyFingerprint;
        State = ChatState.Active;
    }

    public void Apply(EncryptedChatDiscardedEvent aggregateEvent)
    {
        HistoryDeleted = aggregateEvent.DeleteHistory;
        State = ChatState.Discarded;
    }

    public void Apply(EncryptedChatSpamReportedEvent aggregateEvent)
    {
        SpamReporters.Add(aggregateEvent.ReporterId);
    }
}
