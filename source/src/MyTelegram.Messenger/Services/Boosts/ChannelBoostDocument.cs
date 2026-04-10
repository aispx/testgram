namespace MyTelegram.Messenger.Services.Boosts;

public class ChannelBoostDocument
{
    public string Id { get; set; } = default!; // "boost-{channelId}-{userId}-{slot}"
    public long ChannelId { get; set; }
    public long UserId { get; set; }
    public int Slot { get; set; }
    public int Date { get; set; }
    public int Expires { get; set; }
    public int Multiplier { get; set; } = 1;
    public long? Stars { get; set; }
    public bool Gift { get; set; }
    public bool Giveaway { get; set; }
    public bool Unclaimed { get; set; }
}
