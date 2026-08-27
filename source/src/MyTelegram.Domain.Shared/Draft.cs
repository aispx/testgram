namespace MyTelegram;

public class Draft(
    bool noWebpage,
    bool invertMedia,
    int? replyToMsgId,
    string message,
    int date,
    byte[]? entities = null,
    IList<IMessageEntity>? entities2 = null,
    IMessageMedia? media = null,
    int? topMsgId = null,
    long? effect = null,
    IInputMedia? media2 = null,
    IInputReplyTo? replyTo = null,
    ISuggestedPost? suggestedPost = null,
    Peer? savedPeerId = null
)
{
    //bool? invertMedia,

    public string Message { get; init; } = message;
    public bool NoWebpage { get; init; } = noWebpage;
    public bool InvertMedia { get; } = invertMedia;
    public int? ReplyToMsgId { get; init; } = replyToMsgId;
    public int Date { get; init; } = date;
    public byte[]? Entities { get; init; } = entities;
    public IList<IMessageEntity>? Entities2 { get; } = entities2;

    /// <summary>
    /// Never written any more: a draft's media is echoed back to the clients as the
    /// <c>InputMedia</c> they sent (<see cref="Media2"/>), so there is nothing to save. Kept because
    /// drafts stored before that change carry the field.
    /// </summary>
    public IMessageMedia? Media { get; } = media;

    /// <summary>
    /// The <a href="https://corefork.telegram.org/api/forum#forum-topics">forum topic</a> this draft
    /// belongs to, taken from <c>inputReplyToMessage.top_msg_id</c>. Null for a chat level draft.
    /// </summary>
    public int? TopMsgId { get; init; } = topMsgId;
    public long? Effect { get; } = effect;
    public IInputMedia? Media2 { get; } = media2;
    public IInputReplyTo? ReplyTo { get; } = replyTo;
    public ISuggestedPost? SuggestedPost { get; } = suggestedPost;

    /// <summary>
    /// The <a href="https://corefork.telegram.org/api/monoforum">monoforum topic</a> this draft
    /// belongs to, taken from <c>monoforum_peer_id</c>. Null for a chat level draft.
    /// </summary>
    public Peer? SavedPeerId { get; init; } = savedPeerId;
}
