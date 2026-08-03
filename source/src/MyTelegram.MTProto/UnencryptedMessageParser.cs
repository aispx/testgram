namespace MyTelegram.MTProto;

public class UnencryptedMessageParser : IUnencryptedMessageParser, ITransientDependency
{
    public UnencryptedMessage Parse(ReadOnlyMemory<byte> data)
    {
        // auth_key_id(8) + message_id(8) + message_data_length(4)
        const int HeaderLength = 20;

        var span = data.Span;
        if (span.Length < HeaderLength)
        {
            throw new InvalidOperationException(
                $"Unencrypted message is too short: {span.Length} bytes, need at least {HeaderLength}.");
        }

        var offset = 0;
        var authKeyId = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(offset, 8));
        offset += 8;
        var messageId = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(offset, 8));
        offset += 8;
        var messageDataLength = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
        offset += 4;

        // message_data_length is attacker-controlled: it must leave room for at least the 4-byte
        // constructor id read below and must not run past the end of the frame.
        if (messageDataLength < 4 || messageDataLength > span.Length - offset)
        {
            throw new InvalidOperationException(
                $"Invalid message_data_length: {messageDataLength}, available: {span.Length - offset}.");
        }

        var messageData = data.Slice(offset, messageDataLength);
        var objectId = BinaryPrimitives.ReadUInt32LittleEndian(messageData.Span);
        return new UnencryptedMessage(authKeyId,
            string.Empty,
            string.Empty,
            0,
            0,
            messageData,
            messageDataLength,
            messageId,
            objectId,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        );
    }
}
