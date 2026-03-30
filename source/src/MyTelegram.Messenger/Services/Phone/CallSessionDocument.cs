namespace MyTelegram.Messenger.Services.Phone;

public class CallSessionDocument
{
    public long Id { get; set; }
    public long CallId { get; set; }
    public long AccessHash { get; set; }
    public long CallerId { get; set; }
    public long CalleeId { get; set; }
    public int RandomId { get; set; }
    public byte[]? GAHash { get; set; }
    public byte[]? GA { get; set; }
    public byte[]? GB { get; set; }
    public long KeyFingerprint { get; set; }
    public string State { get; set; } = "requested";
    public bool Video { get; set; }
    public int Date { get; set; }
    public int Duration { get; set; }
    public string? DiscardReason { get; set; }
    public string? ProtocolJson { get; set; }
    public string? DebugJson { get; set; }
}
