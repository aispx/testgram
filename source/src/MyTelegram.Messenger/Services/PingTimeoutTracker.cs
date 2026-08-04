using Cube.Timer;

namespace MyTelegram.Messenger.Services;

/// <inheritdoc />
/// <remarks>
/// Backed by the shared <see cref="IScheduleAppService"/> hashed-wheel timer. When a deadline
/// expires we publish a <see cref="PingTimeoutEvent"/>; the gateway subscribes to it (see
/// <c>PingTimeoutEventHandler</c>) and is the only component that owns the socket, so it does
/// the actual close.
/// <para>
/// Nothing here needs to observe <c>ClientDisconnectedEvent</c> (which this process does not
/// subscribe to anyway): every armed timer removes its own entry when it fires, and a plain
/// ping only refreshes an existing entry, so state for a connection that went away is bounded
/// by its own disconnect delay. Publishing the event for an already-closed connection is
/// harmless - the gateway simply fails to find the client data.
/// </para>
/// </remarks>
public class PingTimeoutTracker(IScheduleAppService scheduleAppService, IEventBus eventBus)
    : IPingTimeoutTracker, ISingletonDependency
{
    private sealed record ArmedTimer(long Token, int DelaySeconds, long AuthKeyId, TimerTaskHandle Handle);

    private readonly ConcurrentDictionary<string, ArmedTimer> _timers = new();
    private long _tokenSeed;

    public void Arm(string connectionId, long authKeyId, int disconnectDelaySeconds)
    {
        if (string.IsNullOrEmpty(connectionId))
        {
            return;
        }

        ArmCore(connectionId, authKeyId, disconnectDelaySeconds);
    }

    public void Refresh(string connectionId)
    {
        // Only re-arm if ping_delay_disconnect already established a delay for this connection.
        // A plain ping on a connection that never asked to be disconnected must stay a no-op.
        if (!string.IsNullOrEmpty(connectionId) && _timers.TryGetValue(connectionId, out var armed))
        {
            ArmCore(connectionId, armed.AuthKeyId, armed.DelaySeconds);
        }
    }

    private void ArmCore(string connectionId, long authKeyId, int delaySeconds)
    {
        var token = Interlocked.Increment(ref _tokenSeed);

        // The callback re-reads the dictionary and compares tokens instead of trusting that it
        // was cancelled: TimerTaskHandle.Cancel races with a wheel tick that has already picked
        // the task up, so a superseded timer can still run. Only the newest arming for a
        // connection is allowed to disconnect it.
        var handle = scheduleAppService.Execute(() =>
            {
                if (!_timers.TryGetValue(connectionId, out var current) || current.Token != token)
                {
                    return;
                }

                _timers.TryRemove(new KeyValuePair<string, ArmedTimer>(connectionId, current));
                _ = eventBus.PublishAsync(new PingTimeoutEvent(connectionId, authKeyId));
            },
            TimeSpan.FromSeconds(delaySeconds));

        var next = new ArmedTimer(token, delaySeconds, authKeyId, handle);

        while (true)
        {
            if (_timers.TryGetValue(connectionId, out var previous))
            {
                if (_timers.TryUpdate(connectionId, next, previous))
                {
                    previous.Handle.Cancel();
                    return;
                }
            }
            else if (_timers.TryAdd(connectionId, next))
            {
                return;
            }

            // Another Arm/Refresh for this connection interleaved; retry against its state.
            // The token check in the callback keeps whichever arming loses harmless.
        }
    }

    public void Cancel(string connectionId)
    {
        if (!string.IsNullOrEmpty(connectionId) && _timers.TryRemove(connectionId, out var armed))
        {
            armed.Handle.Cancel();
        }
    }
}
