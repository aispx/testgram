namespace MyTelegram.ReadModel.Interfaces;

public interface IPtsReadModel : IReadModel
{
    int Date { get; }
    long GlobalSeqNo { get; }
    string Id { get; }

    long PeerId { get; }
    int Pts { get; }
    int Qts { get; }

    /// <summary>
    /// The <c>seq</c> reported in <c>updates.state</c>. Clients treat it as a monotonically
    /// increasing counter of update batches and refuse to apply a state whose seq went backwards,
    /// so it must never be left at its default.
    /// </summary>
    int Seq { get; }
    int UnreadCount { get; }
    int MaxMessageId { get; }
}