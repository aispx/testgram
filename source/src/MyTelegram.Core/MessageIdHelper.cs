namespace MyTelegram.Core;
public class MessageIdHelper : IMessageIdHelper, ISingletonDependency
{
    private long _lastMessageId;

    /// <summary>
    ///     How far <see cref="_lastMessageId" /> may run ahead of the clock before it is treated as corrupt
    ///     rather than as "the same millisecond again". One second is <c>1 &lt;&lt; 32</c> in msg_id units, so
    ///     this is five minutes — comfortably more than any legitimate burst, and well inside the ±300 s window
    ///     clients accept (<a href="https://corefork.telegram.org/mtproto/description#message-identifier-msg-id" />).
    /// </summary>
    private const long MaxDriftAheadOfClock = 300L << 32;

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

            // Same millisecond (or a clock that went backwards): keep strictly increasing while
            // preserving the 1 mod 4 residue.
            //
            // Unless the stored value is *implausibly* far ahead, in which case it is not a tie but a
            // corrupt counter — one bad value (a clock that was wrong when the process started, a
            // future-dated value that got in some other way) otherwise pins every id that follows to that
            // bogus time for the lifetime of the process, because each one is only ever `last + 4`. The
            // symptom is a msg_id whose time is frozen years away while the low bits count up, which
            // clients reject outright: Telethon discards such a message ("Server sent a very new
            // message"), so the connection completes the handshake and then goes deaf.
            if (messageId <= last && last - messageId <= MaxDriftAheadOfClock)
            {
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