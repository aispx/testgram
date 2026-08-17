namespace MyTelegram;

public class PeerSettings
{
    public bool AddContact { get; set; }
    public bool BlockContact { get; set; }
    public bool NeedContactsException { get; set; }

    public bool ReportGeo { get; set; }
    public bool ReportSpam { get; set; }
    public bool ShareContact { get; set; }

    public long? ChargePaidMessageStars { get; set; }
    public string? RegistrationMonth { get; set; }
    public string? PhoneCountry { get; set; }
    public int? NameChangeDate { get; set; }
    public int? PhotoChangeDate { get; set; }

    /// <summary>
    /// Set when this conversation was started by the admin of a chat the user recently requested to
    /// join, and that chat is a broadcast channel rather than a group.
    /// See https://corefork.telegram.org/api/invites#join-requests
    /// </summary>
    public bool RequestChatBroadcast { get; set; }

    /// <summary>
    /// Title of the chat the user recently requested to join.
    /// </summary>
    public string? RequestChatTitle { get; set; }

    /// <summary>
    /// When the join request was made.
    /// </summary>
    public int? RequestChatDate { get; set; }
}