namespace MyTelegram.Domain.Aggregates.PushDevice;

public class PushDeviceState : AggregateState<PushDeviceAggregate, PushDeviceId, PushDeviceState>,
    IApply<PushDeviceRegisteredEvent>,
    IApply<PushDeviceUnRegisteredEvent>
{
    public long UserId { get; private set; }
    public long PermAuthKeyId { get; private set; }
    public int TokenType { get; private set; }
    public string? Token { get; private set; }
    public byte[]? Secret { get; private set; }
    public bool NoMuted { get; private set; }
    public bool AppSandbox { get; private set; }
    public IReadOnlyList<long>? OtherUids { get; private set; }
    public DateTimeOffset LastRegisteredAt { get; private set; }
    public bool IsRegistered { get; private set; }

    public void Apply(PushDeviceRegisteredEvent aggregateEvent)
    {
        UserId = aggregateEvent.UserId;
        PermAuthKeyId = aggregateEvent.PermAuthKeyId;
        TokenType = aggregateEvent.TokenType;
        Token = aggregateEvent.Token;
        Secret = aggregateEvent.Secret;
        NoMuted = aggregateEvent.NoMuted;
        AppSandbox = aggregateEvent.AppSandbox;
        OtherUids = aggregateEvent.OtherUids;
        LastRegisteredAt = DateTimeOffset.FromUnixTimeMilliseconds(aggregateEvent.RequestInfo.Date);
        IsRegistered = true;
    }

    public void Apply(PushDeviceUnRegisteredEvent aggregateEvent)
    {
        IsRegistered = false;
    }
}

[EnableAutoGeneration]
public class PushDeviceAggregate : AggregateRoot<PushDeviceAggregate, PushDeviceId>
{
    /// <summary>
    ///     Devices are re-registered (a new event is emitted) at least every 24 hours,
    ///     even when registration parameters are unchanged.
    /// </summary>
    private static readonly TimeSpan ReRegistrationInterval = TimeSpan.FromHours(24);

    private readonly PushDeviceState _state = new();
    public PushDeviceAggregate(PushDeviceId id) : base(id)
    {
        Register(_state);
    }

    [DoNotInheritRequestCommand]
    public void RegisterDevice(RequestInfo requestInfo,
        long userId,
        long permAuthKeyId,
        int tokenType,
        string token,
        bool noMuted,
        bool appSandbox,
        byte[]? secret,
        IReadOnlyList<long>? otherUids)
    {
        // Idempotency (Req 1.4): if the device is already registered with identical parameters
        // and less than 24h has elapsed since the last registration, skip emitting a duplicate
        // event so the resulting read model state matches a single registration.
        if (_state.IsRegistered &&
            IsSameRegistration(userId, tokenType, token, noMuted, appSandbox, secret, otherUids))
        {
            var now = DateTimeOffset.FromUnixTimeMilliseconds(requestInfo.Date);
            if (now - _state.LastRegisteredAt < ReRegistrationInterval)
            {
                return;
            }
        }

        Emit(new PushDeviceRegisteredEvent(requestInfo,
            userId,
            permAuthKeyId,
            tokenType,
            token,
            noMuted,
            appSandbox,
            secret,
            otherUids));
    }

    /// <summary>
    ///     Removes the device binding for the token. Because the aggregate is keyed by token
    ///     (<see cref="PushDeviceId" /> is derived from the token) and the read model is deleted on
    ///     unregistration, this removes the binding for every account the device was addressable to
    ///     (the request <paramref name="otherUids" /> plus the device owner <c>UserId</c>) — multi-account
    ///     routing resolves recipients via <c>OtherUids ∪ {UserId}</c> (Req 3.2).
    /// </summary>
    public void UnRegisterDevice(RequestInfo requestInfo,
        int tokenType,
        string token,
        IReadOnlyList<long> otherUids)
    {
        // Req 3.3: unregistering a token that has no registered device is a no-op that leaves
        // state unchanged. The handler still returns boolTrue; we must not throw here.
        if (IsNew || !_state.IsRegistered)
        {
            return;
        }

        Emit(new PushDeviceUnRegisteredEvent(requestInfo, tokenType, token, otherUids));
    }

    private bool IsSameRegistration(long userId,
        int tokenType,
        string token,
        bool noMuted,
        bool appSandbox,
        byte[]? secret,
        IReadOnlyList<long>? otherUids)
    {
        return _state.UserId == userId &&
               _state.TokenType == tokenType &&
               string.Equals(_state.Token, token, StringComparison.Ordinal) &&
               _state.NoMuted == noMuted &&
               _state.AppSandbox == appSandbox &&
               SecretEquals(_state.Secret, secret) &&
               OtherUidsEquals(_state.OtherUids, otherUids);
    }

    private static bool SecretEquals(byte[]? left, byte[]? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return left.AsSpan().SequenceEqual(right);
    }

    private static bool OtherUidsEquals(IReadOnlyList<long>? left, IReadOnlyList<long>? right)
    {
        var leftEmpty = left is null || left.Count == 0;
        var rightEmpty = right is null || right.Count == 0;
        if (leftEmpty && rightEmpty)
        {
            return true;
        }

        if (leftEmpty || rightEmpty || left!.Count != right!.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }
}
