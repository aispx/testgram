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
    /// Atomically allocates the next qts for (userId, permAuthKeyId) AND registers it as in-flight.
    /// The first allocated value equals <see cref="SecretChatConsts.QtsInitialValue"/>.
    /// <para>
    /// The registration is what keeps <see cref="GetHighestQtsAsync"/> honest, so it must happen in the
    /// same operation as the increment. Every allocation must be released by <see cref="SetQtsAsync"/> or
    /// <see cref="AbandonQtsAsync"/>; one that is never released holds the device's watermark down until
    /// <see cref="SecretChatConsts.QtsAllocationStaleAfter"/> elapses, then the value is burnt.
    /// </para>
    /// </summary>
    Task<int> AllocateQtsAsync(long userId, long permAuthKeyId);

    /// <summary>
    /// Makes an allocated qts visible: writes it onto the message row, then advances the recipient's
    /// delivered watermark and releases the in-flight allocation in one update.
    /// <para>
    /// Returns <c>false</c> when the row had already been sequenced by a concurrent request (the write is
    /// conditional on <c>Qts == 0</c>); the allocation is released and the caller MUST NOT push, or the
    /// recipient would receive the same message twice.
    /// </para>
    /// </summary>
    Task<bool> SetQtsAsync(string id, int qts, long recipientUserId, long recipientPermAuthKeyId);

    /// <summary>
    /// Releases an allocation that will never be written to a row, so it stops holding the watermark down.
    /// Burns the value: qts numbering may contain holes, which is safe because every consumer filters qts
    /// as a range and never requires contiguity.
    /// </summary>
    Task AbandonQtsAsync(int qts, long recipientUserId, long recipientPermAuthKeyId);

    /// <summary>
    /// Highest qts the recipient device can safely be told about:
    /// <c>min(delivered watermark, lowest live in-flight allocation - 1)</c>, and
    /// <c>QtsInitialValue - 1</c> (== 0) when nothing has been delivered to the key yet.
    /// <para>
    /// Invariant: every qts in <c>(QtsInitialValue - 1, returned value]</c> is already written onto its
    /// row and therefore returnable by <see cref="GetForDifferenceAsync"/>. The delivered watermark alone
    /// does NOT satisfy this — it is a <c>$max</c>, so a later allocation committing first would carry it
    /// over an earlier one still in flight and the client would advance past a message that can never be
    /// fetched again. The returned value is monotone non-decreasing.
    /// </para>
    /// </summary>
    Task<int> GetHighestQtsAsync(long userId, long permAuthKeyId);

    /// <summary>
    /// Highest qts ever handed out for the device, in-flight allocations included. Use this — not
    /// <see cref="GetHighestQtsAsync"/> — to validate <c>messages.receivedQueue</c>'s <c>max_qts</c>: a
    /// client may already hold a live-pushed qts that the watermark has not caught up to yet.
    /// </summary>
    Task<int> GetAssignedQtsAsync(long userId, long permAuthKeyId);

    /// <summary>
    /// Acks all unacked messages of the recipient device with qts &lt;= maxQts.
    /// Returns random_ids of messages acked by THIS call only (exact under concurrency).
    /// </summary>
    Task<IReadOnlyList<long>> AckAsync(long userId, long permAuthKeyId, int maxQts);

    /// <summary>
    /// Unacked messages with qts in (sinceQts, maxQts] for updates.getDifference, ordered by qts.
    /// <paramref name="limit"/> caps the result; the caller MUST treat a full page as truncated and
    /// advertise only the qts of the last returned message, otherwise the client would skip the tail.
    /// <paramref name="maxQts"/> must be the watermark from <see cref="GetHighestQtsAsync"/> whenever the
    /// caller advertises one, so a truncated page's cursor can never step over an in-flight allocation.
    /// </summary>
    Task<IReadOnlyList<EncryptedMessageDocument>> GetForDifferenceAsync(long userId,
        long permAuthKeyId,
        int sinceQts,
        int limit,
        int maxQts = int.MaxValue);

    Task DeleteByChatAsync(long chatId);
}
