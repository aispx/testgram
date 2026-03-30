namespace MyTelegram.Domain.Shared;

public interface IMessageEncryptionHelper
{
    bool IsEnabled { get; }
    byte[] Encrypt(long ownerPeerId, string message);
    string Decrypt(long ownerPeerId, int messageId, ReadOnlyMemory<byte> encryptedData);
}
