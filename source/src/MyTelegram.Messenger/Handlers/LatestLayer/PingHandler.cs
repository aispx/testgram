namespace MyTelegram.Messenger.Handlers;

internal sealed class PingHandler(IPingTimeoutTracker pingTimeoutTracker)
    : BaseObjectHandler<RequestPing, IPong>
{
    protected override Task<IPong> HandleCoreAsync(IRequestInput input,
        RequestPing obj)
    {
        // A plain ping proves the client is alive, so push any deadline previously set by
        // ping_delay_disconnect forward. It carries no delay of its own, so it never arms one.
        pingTimeoutTracker.Refresh(input.ConnectionId);

        var r = new TPong { MsgId = input.ReqMsgId, PingId = obj.PingId };
        return Task.FromResult<IPong>(r);
    }
}
