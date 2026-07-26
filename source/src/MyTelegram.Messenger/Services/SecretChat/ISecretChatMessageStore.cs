namespace MyTelegram.Messenger.Services.SecretChat;

public sealed record EncryptedMessageStoreResult(bool IsNew, EncryptedMessageDocument Stored);

/// <summary>
/// Plain-Mongo store for secret-chat messages and the per-Authorization_Key qts sequencer.
/// The <c>encrypted_messages</c> collection is also the per-device temporary update box:
/// only updateNewEncryptedMessage carries qts, and each message has exactly one recipient device.
/// </summary>
public interface ISecretChatMessageStore
{
    Task<EncryptedMessageDocument?> FindAsync(long chatId, long senderUserId, long randomId);

    /// <summary>Insert; on duplicate key returns the previously stored document (IsNew = false).</summary>
    Task<EncryptedMessageStoreResult> StoreAsync(EncryptedMessageDocument document);

    /// <summary>
    /// Atomically allocates the next qts for (userId, permAuthKeyId).
    /// The first allocated value equals <see cref="SecretChatConsts.QtsInitialValue"/>.
    /// </summary>
    Task<int> AllocateQtsAsync(long userId, long permAuthKeyId);

    /// <summary>
    /// Makes an allocated qts visible: writes it onto the message row and only then advances the
    /// recipient's delivered watermark.
    /// </summary>
    Task SetQtsAsync(string id, int qts, long recipientUserId, long recipientPermAuthKeyId);

    /// <summary>
    /// Highest qts the recipient device can actually observe — the DELIVERED watermark, not the
    /// allocator. QtsInitialValue - 1 (== 0) when nothing has been delivered to the key yet.
    /// Advertising the allocator instead would let a client advance past a message whose row is not
    /// yet visible to <see cref="GetForDifferenceAsync"/>, losing it permanently.
    /// </summary>
    Task<int> GetHighestQtsAsync(long userId, long permAuthKeyId);

    /// <summary>
    /// Acks all unacked messages of the recipient device with qts &lt;= maxQts.
    /// Returns random_ids of messages acked by THIS call only (exact under concurrency).
    /// </summary>
    Task<IReadOnlyList<long>> AckAsync(long userId, long permAuthKeyId, int maxQts);

    /// <summary>
    /// Unacked messages with qts &gt; sinceQts for updates.getDifference, ordered by qts.
    /// <paramref name="limit"/> caps the result; the caller MUST treat a full page as truncated and
    /// advertise only the qts of the last returned message, otherwise the client would skip the tail.
    /// </summary>
    Task<IReadOnlyList<EncryptedMessageDocument>> GetForDifferenceAsync(long userId,
        long permAuthKeyId,
        int sinceQts,
        int limit);

    Task DeleteByChatAsync(long chatId);
}
