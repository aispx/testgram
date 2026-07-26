namespace MyTelegram.Messenger.Services.SecretChat;

public class SecretChatRequestDocument
{
    /// <summary>"{AdminId}_{RandomId}"</summary>
    public string Id { get; set; } = null!;

    public long AdminId { get; set; }

    public int RandomId { get; set; }

    public int ChatId { get; set; }

    public long AccessHash { get; set; }

    public long ParticipantId { get; set; }

    public int Date { get; set; }

    public static string BuildId(long adminId, int randomId)
    {
        return $"{adminId}_{randomId}";
    }
}

/// <summary>
/// Durable idempotency ledger for messages.requestEncryption keyed by (adminId, random_id).
/// A read-model query cannot serve this purpose: projections are eventually consistent,
/// while a fast client retry must not create a second chat.
/// </summary>
public interface ISecretChatRequestLedger
{
    Task<SecretChatRequestDocument?> FindAsync(long adminId, int randomId);

    /// <summary>Insert; on duplicate key returns the previously reserved document.</summary>
    Task<SecretChatRequestDocument> ReserveAsync(SecretChatRequestDocument document);
}
