namespace MyTelegram.Converters.TLObjects.LatestLayer;

public class EncryptedChatConverter : IEncryptedChatConverter, ITransientDependency
{
    public virtual int Layer => Layers.LayerLatest;

    public virtual IEncryptedChat ToEncryptedChatWaiting(long chatId,
        long accessHash,
        int date,
        long adminId,
        long participantId)
    {
        return new TEncryptedChatWaiting
        {
            Id = (int)chatId,
            AccessHash = accessHash,
            Date = date,
            AdminId = adminId,
            ParticipantId = participantId
        };
    }

    public virtual IEncryptedChat ToEncryptedChatWaiting(IEncryptedChatReadModel chat)
    {
        return ToEncryptedChatWaiting(chat.ChatId, chat.AccessHash, chat.Date, chat.AdminId, chat.ParticipantId);
    }

    public virtual IEncryptedChat ToEncryptedChatRequested(long chatId,
        long accessHash,
        int date,
        long adminId,
        long participantId,
        byte[] ga)
    {
        return new TEncryptedChatRequested
        {
            Id = (int)chatId,
            AccessHash = accessHash,
            Date = date,
            AdminId = adminId,
            ParticipantId = participantId,
            GA = ga
        };
    }

    public virtual IEncryptedChat ToEncryptedChatRequested(IEncryptedChatReadModel chat)
    {
        return ToEncryptedChatRequested(chat.ChatId, chat.AccessHash, chat.Date, chat.AdminId, chat.ParticipantId,
            chat.Ga);
    }

    public virtual IEncryptedChat ToEncryptedChat(long chatId,
        long accessHash,
        int date,
        long adminId,
        long participantId,
        byte[] gaOrB,
        long keyFingerprint)
    {
        return new TEncryptedChat
        {
            Id = (int)chatId,
            AccessHash = accessHash,
            Date = date,
            AdminId = adminId,
            ParticipantId = participantId,
            GAOrB = gaOrB,
            KeyFingerprint = keyFingerprint
        };
    }

    public virtual IEncryptedChat ToEncryptedChat(IEncryptedChatReadModel chat, long callerUserId)
    {
        // The admin needs the participant's g_b, the participant needs the admin's g_a.
        var gaOrB = callerUserId == chat.AdminId ? chat.Gb : chat.Ga;

        return ToEncryptedChat(chat.ChatId, chat.AccessHash, chat.Date, chat.AdminId, chat.ParticipantId, gaOrB,
            chat.KeyFingerprint);
    }

    public virtual IEncryptedChat ToEncryptedChatDiscarded(long chatId, bool historyDeleted)
    {
        return new TEncryptedChatDiscarded
        {
            Id = (int)chatId,
            HistoryDeleted = historyDeleted
        };
    }

    public virtual IUpdate ToUpdateEncryption(IEncryptedChat chat, int date)
    {
        return new TUpdateEncryption
        {
            Chat = chat,
            Date = date
        };
    }
}
