namespace MyTelegram.Domain.Aggregates.EncryptedChat;

/// <summary>
/// Secret chat lifecycle aggregate ("blind relay" model).
/// The server stores only the opaque DH values (g_a/g_b) and key fingerprint,
/// never a private exponent or the shared key.
/// State machine: (new) -> Waiting -> Active -> Discarded; Waiting -> Discarded; Discarded is terminal.
/// ChatState.Requested is a per-role view computed by converters and is never stored.
/// </summary>
[EnableAutoGeneration]
public class EncryptedChatAggregate : AggregateRoot<EncryptedChatAggregate, EncryptedChatId>
{
    private readonly EncryptedChatState _state = new();

    public EncryptedChatAggregate(EncryptedChatId id) : base(id)
    {
        Register(_state);
    }

    public void CreateEncryptedChat(int chatId,
        long adminId,
        long participantId,
        long adminPermAuthKeyId,
        long accessHash,
        byte[] ga,
        int randomId,
        int date)
    {
        Specs.AggregateIsNew.ThrowFirstDomainErrorIfNotSatisfied(this);

        Emit(new EncryptedChatCreatedEvent(chatId,
            adminId,
            participantId,
            adminPermAuthKeyId,
            accessHash,
            ga,
            randomId,
            date));
    }

    public void AcceptEncryptedChat(long callerId,
        long participantPermAuthKeyId,
        byte[] gb,
        long keyFingerprint,
        int date)
    {
        Specs.AggregateIsCreated.ThrowFirstDomainErrorIfNotSatisfied(this);

        if (_state.State == ChatState.Discarded)
        {
            RpcErrors.RpcErrors400.EncryptionAlreadyDeclined.ThrowRpcError();
        }

        if (_state.State == ChatState.Active)
        {
            RpcErrors.RpcErrors400.EncryptionAlreadyAccepted.ThrowRpcError();
        }

        if (callerId != _state.ParticipantId)
        {
            RpcErrors.RpcErrors400.EncryptionIdInvalid.ThrowRpcError();
        }

        Emit(new EncryptedChatAcceptedEvent(participantPermAuthKeyId, gb, keyFingerprint, date));
    }

    public void DiscardEncryptedChat(long callerId,
        bool deleteHistory,
        int date)
    {
        Specs.AggregateIsCreated.ThrowFirstDomainErrorIfNotSatisfied(this);

        if (_state.State == ChatState.Discarded)
        {
            RpcErrors.RpcErrors400.EncryptionAlreadyDeclined.ThrowRpcError();
        }

        if (callerId != _state.AdminId && callerId != _state.ParticipantId)
        {
            RpcErrors.RpcErrors400.EncryptionIdInvalid.ThrowRpcError();
        }

        Emit(new EncryptedChatDiscardedEvent(callerId, deleteHistory, date));
    }

    public void ReportEncryptedChatSpam(long reporterId)
    {
        Specs.AggregateIsCreated.ThrowFirstDomainErrorIfNotSatisfied(this);

        if (reporterId != _state.AdminId && reporterId != _state.ParticipantId)
        {
            RpcErrors.RpcErrors400.EncryptionIdInvalid.ThrowRpcError();
        }

        if (_state.SpamReporters.Contains(reporterId))
        {
            // Idempotent: at most one report per caller, repeated calls have no effect.
            return;
        }

        Emit(new EncryptedChatSpamReportedEvent(reporterId));
    }
}
