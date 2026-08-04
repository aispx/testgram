namespace MyTelegram.Messenger.Services;

public class SearchGlobalInput : GetPagedListInput
{
    public int? FolderId { get; set; }
    public bool IsSearchGlobal => true;
    public MessageType MessageType { get; set; }
    public long OwnerPeerId { get; set; }
    public string Q { get; set; } = default!;
    public long SelfUserId { get; set; }

    public List<long> JoinedChannelList { get; set; } = [];

    public bool BroadcastsOnly { get; set; }
    public bool GroupsOnly { get; set; }
    public bool UsersOnly { get; set; }
    public List<long>? Tokens { get; set; }

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
    /// offset_rate from the previous page: the date of the last message returned.
    /// See https://corefork.telegram.org/method/messages.searchGlobal
    /// </summary>
    public int OffsetRate { get; set; }
}