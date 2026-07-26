namespace MyTelegram.ReadModel.Interfaces;

public interface IEncryptedChatReadModel : IReadModel
{
    long AccessHash { get; }
    long AdminPermAuthKeyId { get; }
    long AdminId { get; }

    long ChatId { get; }
    ChatState ChatState { get; }
    int Date { get; }
    byte[] Ga { get; }
    byte[] Gb { get; }
    bool HistoryDeleted { get; }
    string Id { get; }
    long KeyFingerprint { get; }
    long ParticipantPermAuthKeyId { get; }
    long ParticipantId { get; }
    long RandomId { get; }
    List<long> SpamReporters { get; }
}
