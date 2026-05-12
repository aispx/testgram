namespace MyTelegram.Messenger.Services;

public class GetHistoryInput : GetPagedListInput
{
    public int ChannelHistoryMinId { get; set; }
    public long OwnerPeerId { get; set; }
    public Peer Peer { get; set; } = default!;
    public long SelfUserId { get; set; }
    public long FilterSenderUserId { get; set; }
    public Peer? SavedPeerId { get; set; }
}