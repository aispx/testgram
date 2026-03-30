namespace MyTelegram.Messenger.Services.Phone;

public class GroupCallDocument
{
    public long Id { get; set; }
    public long CallId { get; set; }
    public long AccessHash { get; set; }
    public long PeerId { get; set; }
    public int PeerType { get; set; }
    public bool Active { get; set; } = true;
    public bool JoinMuted { get; set; }
    public bool RtmpStream { get; set; }
    public int Version { get; set; } = 1;
    public int Date { get; set; }
    public List<GroupCallParticipantDoc> Participants { get; set; } = new();
}

public class GroupCallParticipantDoc
{
    public long PeerId { get; set; }
    public int PeerType { get; set; }
    public int Source { get; set; }
    public bool Muted { get; set; }
    public bool VideoStopped { get; set; }
    public int Date { get; set; }
    public string? ParamsJson { get; set; }
}
