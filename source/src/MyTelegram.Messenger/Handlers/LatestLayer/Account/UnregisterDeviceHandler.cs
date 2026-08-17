using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Deletes a device by its token, stops sending PUSH-notifications to it.
/// Possible errors
/// Code Type Description
/// 400 TOKEN_INVALID The provided token is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.unregisterDevice"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class UnregisterDeviceHandler(ICommandBus commandBus, IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestUnregisterDevice, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestUnregisterDevice obj)
    {
        // The device row is scoped to (token, account), so this only ever targets the caller's own
        // registration — a foreign account's row for the same token is a different aggregate and cannot
        // be touched here. The ownership lookup keeps the documented "unknown token = silent no-op"
        // behaviour. Provider-driven cleanup (PushNotificationEventHandler) publishes the command
        // directly and does not go through this gate.
        var callerDevices = await queryProcessor.ProcessAsync(new GetPushDevicesForRecipientQuery(input.UserId));
        var ownsDevice = callerDevices.Any(p => string.Equals(p.Token, obj.Token, StringComparison.Ordinal));
        if (!ownsDevice)
        {
            // Behave like unregistering an unknown token: a silent no-op, so we neither delete a
            // foreign device nor reveal that the token belongs to someone else.
            return new TBoolTrue();
        }

        var command = new UnRegisterDeviceCommand(PushDeviceId.Create(obj.Token, input.UserId), input.ToRequestInfo(), obj.TokenType, obj.Token, obj.OtherUids.ToList());
        await commandBus.PublishAsync(command, CancellationToken.None);
        return new TBoolTrue();
    }
}