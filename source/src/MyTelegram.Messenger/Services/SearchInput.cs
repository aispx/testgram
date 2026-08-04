namespace MyTelegram.Messenger.Services;

public class SearchInput : GetPagedListInput
{
    public MessageType MessageType { get; set; }
    public long OwnerPeerId { get; set; }
    public Peer Peer { get; set; } = default!;
    public string Q { get; set; } = default!;
    public long SelfUserId { get; set; }
    public List<long>? Tokens { get; set; }
    public long FilterSenderUserId { get; set; }
    public Peer? SavedPeerId { get; set; }
    public int TopMsgId { get; set; }

    /// <summary>
    /// Message types matching the requested filter. See MessageFilterHelper - a filter maps to a
    /// set of types, not a single one.
    /// </summary>
    public List<MessageType>? MessageTypes { get; set; }

    /// <summary>
    /// inputMessagesFilterMyMentions: resolved from message entities after the query runs.
    /// </summary>
    public bool MyMentionsOnly { get; set; }

    /// <summary>
    /// saved_reaction: only return saved messages tagged with these reactions. Only meaningful
    /// together with <see cref="SavedPeerId"/>.
    /// See https://corefork.telegram.org/api/saved-messages#tags
    /// </summary>
    public TVector<IReaction>? SavedReaction { get; set; }
}