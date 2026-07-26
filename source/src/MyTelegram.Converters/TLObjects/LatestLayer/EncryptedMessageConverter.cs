namespace MyTelegram.Converters.TLObjects.LatestLayer;

public class EncryptedMessageConverter : IEncryptedMessageConverter, ITransientDependency
{
    public virtual int Layer => Layers.LayerLatest;

    public virtual IEncryptedMessage ToEncryptedMessage(IEncryptedMessageReadModel messageReadModel,
        IEncryptedFile? file)
    {
        return new TEncryptedMessage
        {
            RandomId = messageReadModel.RandomId,
            ChatId = (int)messageReadModel.ChatId,
            Date = messageReadModel.Date,
            Bytes = messageReadModel.Data,
            // encryptedMessage.file is non-optional in TL: null maps to encryptedFileEmpty.
            File = file ?? new TEncryptedFileEmpty()
        };
    }

    public virtual IEncryptedMessage ToEncryptedMessageService(IEncryptedMessageReadModel messageReadModel)
    {
        return new TEncryptedMessageService
        {
            RandomId = messageReadModel.RandomId,
            ChatId = (int)messageReadModel.ChatId,
            Date = messageReadModel.Date,
            Bytes = messageReadModel.Data
        };
    }

    public virtual IUpdate ToUpdateNewEncryptedMessage(IEncryptedMessage message, int qts)
    {
        return new TUpdateNewEncryptedMessage
        {
            Message = message,
            Qts = qts
        };
    }

    public virtual IUpdate ToUpdateEncryptedChatTyping(long chatId)
    {
        return new TUpdateEncryptedChatTyping
        {
            ChatId = (int)chatId
        };
    }

    public virtual IUpdate ToUpdateEncryptedMessagesRead(long chatId, int maxDate, int date)
    {
        return new TUpdateEncryptedMessagesRead
        {
            ChatId = (int)chatId,
            MaxDate = maxDate,
            Date = date
        };
    }
}
