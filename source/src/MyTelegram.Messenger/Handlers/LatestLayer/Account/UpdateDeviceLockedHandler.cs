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
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestUpdateDeviceLocked obj)
    {
        await deviceLockStore.SetAsync(input.PermAuthKeyId, obj.Period);

        return new TBoolTrue();
    }
}
