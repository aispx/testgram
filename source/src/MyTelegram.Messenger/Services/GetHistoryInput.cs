namespace MyTelegram.Messenger.Services;

public class GetHistoryInput : GetPagedListInput
{
    public int ChannelHistoryMinId { get; set; }
    public long OwnerPeerId { get; set; }
    public Peer Peer { get; set; } = default!;
    public long SelfUserId { get; set; }
    public long FilterSenderUserId { get; set; }
    public Peer? SavedPeerId { get; set; }

    /// <summary>
    /// Restricts the page to a single kind of message. Defaults to <see cref="MessageType.Unknown"/>,
    /// which means "no filter" and keeps plain history paging unchanged; used by
    /// <c>messages.getRecentLocations</c> to page over geo messages only.
    /// </summary>
    public MessageType MessageType { get; set; } = MessageType.Unknown;

    /// <summary>
    /// Restricts the page to live locations. <see cref="MessageType.Geo"/> also matches static
    /// locations and venues, so <c>messages.getRecentLocations</c> needs this to keep those from
    /// filling the window. See https://corefork.telegram.org/api/live-location
    /// </summary>
    public bool GeoLiveOnly { get; set; }
}