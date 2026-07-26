namespace MyTelegram.Converters.TLObjects.Interfaces;

public interface IEncryptedMessageConverter : ILayeredConverter
{
    IEncryptedMessage ToEncryptedMessage(IEncryptedMessageReadModel messageReadModel, IEncryptedFile? file);

    IEncryptedMessage ToEncryptedMessageService(IEncryptedMessageReadModel messageReadModel);

    IUpdate ToUpdateNewEncryptedMessage(IEncryptedMessage message, int qts);

    IUpdate ToUpdateEncryptedChatTyping(long chatId);

    IUpdate ToUpdateEncryptedMessagesRead(long chatId, int maxDate, int date);
}
