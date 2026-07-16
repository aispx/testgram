namespace MyTelegram.Messenger.Services.Stats;

/// <summary>
/// The channel kind a stats method requires, enforced by the Access_Controller.
/// </summary>
public enum StatsChannelKind
{
    /// <summary>Any channel kind is accepted.</summary>
    Any,

    /// <summary>Only broadcast channels are accepted (otherwise <c>BROADCAST_REQUIRED</c>).</summary>
    BroadcastOnly,

    /// <summary>Only megagroups/supergroups are accepted (otherwise <c>MEGAGROUP_REQUIRED</c>).</summary>
    MegagroupOnly
}
