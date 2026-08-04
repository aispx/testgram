namespace MyTelegram.Messenger.Handlers;

/// <summary>
/// Ping with a disconnect deadline: if the client sends nothing for <c>disconnect_delay</c>
/// seconds, the server drops the connection.
/// <para><c>See <a href="https://corefork.telegram.org/method/ping_delay_disconnect"/> </c></para>
/// <para><c>See <a href="https://corefork.telegram.org/api/optimisation"/> </c></para>
/// </summary>
internal sealed class PingDelayDisconnectHandler(IPingTimeoutTracker pingTimeoutTracker)
    : BaseObjectHandler<RequestPingDelayDisconnect, IPong>
{
    /// <summary>
    /// Official clients ask for 75s (see tdlib ConnectionsManager); clamp to keep a hostile or
    /// buggy client from either pinning a connection open forever or having the server hang up
    /// on it immediately with disconnect_delay=0.
    /// </summary>
    private const int MinDisconnectDelay = 1;
    private const int MaxDisconnectDelay = 75;

    protected override Task<IPong> HandleCoreAsync(IRequestInput input,
        RequestPingDelayDisconnect obj)
    {
        pingTimeoutTracker.Arm(input.ConnectionId, input.AuthKeyId,
            Math.Clamp(obj.DisconnectDelay, MinDisconnectDelay, MaxDisconnectDelay));

        var r = new TPong { MsgId = input.ReqMsgId, PingId = obj.PingId };
        return Task.FromResult<IPong>(r);
    }
}
