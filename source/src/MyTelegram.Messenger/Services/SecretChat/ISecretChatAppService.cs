namespace MyTelegram.Messenger.Services.SecretChat;

/// <summary>
/// Orchestrates all secret-chat RPC methods (blind relay).
/// See https://corefork.telegram.org/api/end-to-end
/// </summary>
public interface ISecretChatAppService
{
    Task<IEncryptedChat> RequestEncryptionAsync(IRequestInput input, IInputUser userId, int randomId, byte[] ga);

    Task<IEncryptedChat> AcceptEncryptionAsync(IRequestInput input,
        IInputEncryptedChat peer,
        byte[] gb,
        long keyFingerprint);

    Task<IBool> DiscardEncryptionAsync(IRequestInput input, int chatId, bool deleteHistory);

    Task<MyTelegram.Schema.Messages.ISentEncryptedMessage> SendEncryptedAsync(IRequestInput input,
        IInputEncryptedChat peer,
        long randomId,
        ReadOnlyMemory<byte> data,
        bool silent);

    Task<MyTelegram.Schema.Messages.ISentEncryptedMessage> SendEncryptedFileAsync(IRequestInput input,
        IInputEncryptedChat peer,
        long randomId,
        ReadOnlyMemory<byte> data,
        IInputEncryptedFile file,
        bool silent);

    Task<MyTelegram.Schema.Messages.ISentEncryptedMessage> SendEncryptedServiceAsync(IRequestInput input,
        IInputEncryptedChat peer,
        long randomId,
        ReadOnlyMemory<byte> data);

    Task<IBool> ReadEncryptedHistoryAsync(IRequestInput input, IInputEncryptedChat peer, int maxDate);

    Task<IBool> SetEncryptedTypingAsync(IRequestInput input, IInputEncryptedChat peer, bool typing);

    Task<IEncryptedFile> UploadEncryptedFileAsync(IRequestInput input,
        IInputEncryptedChat peer,
        IInputEncryptedFile file);

    Task<TVector<long>> ReceivedQueueAsync(IRequestInput input, int maxQts);

    Task<IBool> ReportEncryptedSpamAsync(IRequestInput input, IInputEncryptedChat peer);
}
