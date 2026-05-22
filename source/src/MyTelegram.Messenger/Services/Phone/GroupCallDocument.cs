namespace MyTelegram.Messenger.Services.Phone;

public class GroupCallDocument
{
    public long Id { get; set; }
    public long CallId { get; set; }
    public long AccessHash { get; set; }
    public int RandomId { get; set; }
    public long PeerId { get; set; }
    public int PeerType { get; set; }
    public long CreatorId { get; set; }
    public bool Active { get; set; } = true;
    public bool JoinMuted { get; set; }
    public bool RtmpStream { get; set; }
    public bool Conference { get; set; }
    public bool MessagesEnabled { get; set; } = true;
    public bool RecordVideoActive { get; set; }
    public string? RecordTitle { get; set; }
    public string? Title { get; set; }
    public int? ScheduleDate { get; set; }
    public int? RecordStartDate { get; set; }
    public long? SendPaidMessagesStars { get; set; }
    public string? InviteHash { get; set; }
    public string? InviteLink { get; set; }
    public long? DefaultSendAsPeerId { get; set; }
    public int? DefaultSendAsPeerType { get; set; }
    public int Version { get; set; } = 1;
    public int Date { get; set; }
    public List<GroupCallParticipantDoc> Participants { get; set; } = new();
    public List<long> InvitedUserIds { get; set; } = new();
    public List<long> ScheduleStartSubscriberIds { get; set; } = new();
    public List<GroupCallChainBlockDoc> ChainBlocks { get; set; } = new();
}

public class GroupCallParticipantDoc
{
    public long PeerId { get; set; }
    public int PeerType { get; set; }
    public int Source { get; set; }
    public bool Muted { get; set; }
    public bool VideoStopped { get; set; }
    public bool VideoPaused { get; set; }
    public bool PresentationPaused { get; set; }
    public bool RaiseHand { get; set; }
    public int? Volume { get; set; }
    public int Date { get; set; }
    public string? ParamsJson { get; set; }
    public string? PresentationParamsJson { get; set; }
    public byte[]? PublicKey { get; set; }
}

public class GroupCallChainBlockDoc
{
    public int SubChainId { get; set; }
    public byte[] Block { get; set; } = [];
}
