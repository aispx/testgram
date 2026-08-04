namespace MyTelegram.Messenger.Services;

/// <summary>
/// Tracks the <c>disconnect_delay</c> requested via <c>ping_delay_disconnect</c> and closes the
/// connection when the client stops pinging.
/// <para><c>See <a href="https://corefork.telegram.org/api/optimisation"/> </c></para>
/// </summary>
public interface IPingTimeoutTracker
{
    /// <summary>
    /// (Re)arms the disconnect timer for a connection with the delay the client asked for. Any
    /// previously armed timer for the same connection is cancelled, so each
    /// <c>ping_delay_disconnect</c> pushes the deadline forward.
    /// </summary>
    void Arm(string connectionId, long authKeyId, int disconnectDelaySeconds);

    /// <summary>
    /// Pushes an already-armed deadline forward by the delay that was last requested for this
    /// connection, without changing that delay. Used by plain <c>ping</c>: it proves the client
    /// is alive, but unlike <c>ping_delay_disconnect</c> it carries no new delay of its own.
    /// Does nothing when no timer is armed - a plain ping must never start one.
    /// </summary>
    void Refresh(string connectionId);

    /// <summary>
    /// Cancels the disconnect timer for a connection, if one is armed.
    /// </summary>
    void Cancel(string connectionId);
}
