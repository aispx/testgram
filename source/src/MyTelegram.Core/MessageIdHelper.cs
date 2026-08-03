namespace MyTelegram.Core;
public class MessageIdHelper : IMessageIdHelper, ISingletonDependency
{
    private long _lastMessageId;
    //private readonly long _timeDelta = 0;

    /// <summary>
    ///     Generates a server message id. This type is a singleton shared by every connection, so the
    ///     read-modify-write of <see cref="_lastMessageId" /> is done with a compare-and-swap loop: an
    ///     unsynchronised version can hand the same msg_id to two concurrent handshakes.
    /// </summary>
    /// <remarks>
    ///     https://corefork.telegram.org/mtproto/description#message-identifier-msg-id — the upper 32 bits are
    ///     the unix time and the lower 32 bits approximate the fractional part of the second. The result is
    ///     always 1 mod 4, which marks a server response to a client query; this generator is only used for
    ///     handshake replies (res_pq, server_DH_params_ok, dh_gen_ok), which are exactly that.
    /// </remarks>
    public long GenerateMessageId()
    {
        while (true)
        {
            var last = Interlocked.Read(ref _lastMessageId);

            var unixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var seconds = unixTimeMs / 1000;
            var fraction = ((unixTimeMs % 1000) << 32) / 1000;

            // Clear the low two bits and set msg_id mod 4 == 1.
            var messageId = (((seconds << 32) | fraction) & ~3L) | 1;

            if (messageId <= last)
            {
                // Same millisecond (or a clock that went backwards): keep strictly increasing while
                // preserving the 1 mod 4 residue.
                messageId = last + 4;
            }

            if (Interlocked.CompareExchange(ref _lastMessageId, messageId, last) == last)
            {
                return messageId;
            }
        }
    }

    public long GenerateUniqueId()
    {
        return BitConverter.ToInt64(Guid.NewGuid().ToByteArray());
    }
}