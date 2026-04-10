namespace MyTelegram.Messenger.Services.Boosts;

public class UserBoostSlotDocument
{
    public string Id { get; set; } = default!; // "boost-slot-{userId}-{slot}"
    public long UserId { get; set; }
    public int Slot { get; set; }
    public int Cooldown { get; set; }
    public long? ChannelId { get; set; }
}
