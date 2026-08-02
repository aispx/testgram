namespace MyTelegram.Messenger.Services.SecretChat;

/// <summary>
/// A stored secret-chat message. The single Mongo collection <c>encrypted_messages</c> doubles
/// as the per-device qts update box: each row targets exactly one recipient Authorization_Key.
/// The payload (<see cref="Data"/>) is an opaque blob relayed verbatim (blind relay).
/// </summary>
public class EncryptedMessageDocument : IEncryptedMessageReadModel
{
    /// <summary>"{ChatId}_{SenderUserId}_{RandomId}" — atomic, permanent dedup key.</summary>
    public string Id { get; set; } = null!;

    public long ChatId { get; set; }

    /// <summary>Sender user id.</summary>
    public long UserId { get; set; }

    /// <summary>Sender's permanent auth key id.</summary>
    public long PermAuthKeyId { get; set; }

    public long RecipientUserId { get; set; }

    public long RecipientPermAuthKeyId { get; set; }

    /// <summary>Opaque encrypted payload, stored and relayed byte-identically.</summary>
    public byte[] Data { get; set; } = [];

    /// <summary>Serialized TL bytes of the resolved IEncryptedFile, or null for messages without a file.</summary>
    public byte[]? File { get; set; }

    public int Date { get; set; }

    /// <summary>
    /// Insertion time, carried as a BSON date purely so the retention TTL index can expire the row
    /// (<see cref="Date"/> is a unix-seconds int, which MongoDB's TTL cannot use).
    /// </summary>
    public DateTime CreatedAt { get; set; }

    public SendMessageType MessageType { get; set; }

    /// <summary>Recipient-device qts. 0 = not yet assigned (crash between insert and allocation).</summary>
    public int Qts { get; set; }

    public long RandomId { get; set; }

    public bool Acked { get; set; }

    public int AckedDate { get; set; }

    public static string BuildId(long chatId, long senderUserId, long randomId)
    {
        return $"{chatId}_{senderUserId}_{randomId}";
    }
}
