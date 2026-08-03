namespace MyTelegram.Messenger.Services.Phone;

/// <summary>
/// The <see cref="CallSessionDocument.State"/> values of the 1:1 call state machine:
/// <c>requested</c> → <c>received</c> → <c>accepted</c> → <c>confirmed</c> → <c>discarded</c>.
/// </summary>
public static class CallSessionStates
{
    /// <summary>phone.requestCall has been accepted by the server; the callee has not acknowledged yet.</summary>
    public const string Requested = "requested";

    /// <summary>The callee's device acknowledged the incoming call via phone.receivedCall (it is ringing).</summary>
    public const string Received = "received";

    /// <summary>The callee answered via phone.acceptCall and supplied g_b.</summary>
    public const string Accepted = "accepted";

    /// <summary>The caller revealed g_a via phone.confirmCall; the call is connected.</summary>
    public const string Confirmed = "confirmed";

    /// <summary>Terminal state.</summary>
    public const string Discarded = "discarded";

    /// <summary>
    /// Every non-terminal state. A user with a session in any of these is busy and cannot start or
    /// receive another call.
    /// </summary>
    public static readonly string[] Live = [Requested, Received, Accepted, Confirmed];
}
