namespace MyTelegram.MTProto;

public class EncryptedMessageParser : IEncryptedMessageParser, ITransientDependency
{
    /// <summary>
    ///     auth_key_id(8) + msg_key(16) + at least one 16-byte AES block of encrypted_data.
    /// </summary>
    private const int MinLength = 8 + 16 + 16;

    public EncryptedMessage Parse(ReadOnlyMemory<byte> data)
    {
        if (data.Length < MinLength)
        {
            throw new InvalidOperationException(
                $"Encrypted message is too short: {data.Length} bytes, need at least {MinLength}.");
        }

        var authKeyId = BinaryPrimitives.ReadInt64LittleEndian(data.Span);
        var msgKey = data.Slice(8, 16);
        var encryptedData = data[(8 + 16)..];
        return new EncryptedMessage(authKeyId, msgKey, encryptedData, string.Empty, ConnectionType.UnKnown, 0,
            string.Empty, Guid.NewGuid(), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }
}
