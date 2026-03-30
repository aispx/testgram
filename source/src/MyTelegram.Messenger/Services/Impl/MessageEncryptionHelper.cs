using System.Text;
using Microsoft.Extensions.Options;
using MyTelegram.Domain.Shared;

namespace MyTelegram.Messenger.Services.Impl;

public class MessageEncryptionHelper(IDataEncryptionHelper dataEncryptionHelper, IOptionsMonitor<MyTelegramMessengerServerOptions> options, IMessageConverterService messageConverterService) : IMessageEncryptionHelper, ISingletonDependency
{
    private KeyConfig? _keyConfig;
    private byte[]? _encryptionKey;

    public bool IsEnabled => options.CurrentValue.EncryptionConfig is { Enabled: true, MessageKeys.Count: > 0 };

    public byte[] Encrypt(long ownerPeerId, string message)
    {
        _keyConfig ??= options.CurrentValue.EncryptionConfig.MessageKeys[0];
        _encryptionKey ??= Encoding.UTF8.GetBytes(_keyConfig.Key);
        return dataEncryptionHelper.Encrypt(_keyConfig.Id, _encryptionKey, ownerPeerId, message);
    }

    public string Decrypt(long ownerPeerId, int messageId, ReadOnlyMemory<byte> encryptedData)
    {
        return messageConverterService.DecryptMessage(ownerPeerId, messageId, encryptedData);
    }
}
