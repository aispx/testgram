namespace MyTelegram.Domain.Aggregates.EncryptedChat;

public class EncryptedChatId(string value) : Identity<EncryptedChatId>(value)
{
    public static EncryptedChatId Create(int chatId)
    {
        return NewDeterministic(GuidFactories.Deterministic.Namespaces.Commands, $"encrypted_chat_{chatId}");
    }
}
