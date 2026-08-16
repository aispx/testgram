using MyTelegram.Messenger.Services.Push;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// When client-side passcode lock feature is enabled, will not show message texts in incoming <a href="https://corefork.telegram.org/api/push-updates">PUSH notifications</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.updateDeviceLocked"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class UpdateDeviceLockedHandler(IDeviceLockStore deviceLockStore) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestUpdateDeviceLocked, IBool>
{
    /// <summary>
    /// Upper bound for the passcode-lock masking window. The value is otherwise stored as a Redis TTL
    /// in seconds, so an unclamped int.MaxValue would pin the caller's push texts hidden for ~68 years.
    /// The scope is the caller's own device and the failure mode is fail-safe, but the TTL still needs a
    /// sane ceiling.
    /// </summary>
    private const int MaxPeriodSeconds = 86400;

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestUpdateDeviceLocked obj)
    {
        var period = Math.Clamp(obj.Period, 0, MaxPeriodSeconds);
        await deviceLockStore.SetAsync(input.PermAuthKeyId, period);

        return new TBoolTrue();
    }
}
