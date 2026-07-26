namespace MyTelegram.Converters.TLObjects.Interfaces;

public interface IEncryptedChatConverter : ILayeredConverter
{
    IEncryptedChat ToEncryptedChatWaiting(long chatId, long accessHash, int date, long adminId, long participantId);
    IEncryptedChat ToEncryptedChatWaiting(IEncryptedChatReadModel chat);

    IEncryptedChat ToEncryptedChatRequested(long chatId,
        long accessHash,
        int date,
        long adminId,
        long participantId,
        byte[] ga);

    IEncryptedChat ToEncryptedChatRequested(IEncryptedChatReadModel chat);

    IEncryptedChat ToEncryptedChat(long chatId,
        long accessHash,
        int date,
        long adminId,
        long participantId,
        byte[] gaOrB,
        long keyFingerprint);

    /// <summary>
    /// g_a_or_b is selected by caller role: the admin sees g_b, the participant sees g_a.
    /// </summary>
    IEncryptedChat ToEncryptedChat(IEncryptedChatReadModel chat, long callerUserId);

    IEncryptedChat ToEncryptedChatDiscarded(long chatId, bool historyDeleted);

    IUpdate ToUpdateEncryption(IEncryptedChat chat, int date);
}
