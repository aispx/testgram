namespace MyTelegram.Messenger.Services;

public class PrivateChatNoForwardsDocument
{
    public long OwnerUserId { get; set; }
    public long PeerUserId { get; set; }
    public bool Enabled { get; set; }
    public int UpdatedAt { get; set; }
}
