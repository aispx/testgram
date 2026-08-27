namespace MyTelegram;

/// <summary>
/// Which draft of a peer is meant: the chat itself, one forum topic, or one
/// <a href="https://corefork.telegram.org/api/monoforum">monoforum</a> topic. See
/// <see cref="DraftTopicKey"/> for how the three are told apart in storage.
/// </summary>
public record DraftTopic(int? TopMsgId = null, Peer? SavedPeerId = null)
{
    /// <summary>The draft of the chat itself, the one <c>dialog.draft</c> is built from.</summary>
    public static DraftTopic ChatLevel { get; } = new();

    public string Key => DraftTopicKey.Create(TopMsgId, SavedPeerId);

    public bool IsChatLevel => DraftTopicKey.IsChatLevel(Key);
}
