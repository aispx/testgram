using MyTelegram.Domain.Aggregates.Device;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Auth;
/// <summary>
/// Delete all temporary authorization keys <strong>except for</strong> the ones specified
/// <para><c>See <a href="https://corefork.telegram.org/method/auth.dropTempAuthKeys"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class DropTempAuthKeysHandler(
    IQueryProcessor queryProcessor,
    ICommandBus commandBus,
    IEventBus eventBus)
    : RpcResultObjectHandler<MyTelegram.Schema.Auth.RequestDropTempAuthKeys, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Auth.RequestDropTempAuthKeys obj)
    {
        if (input.UserId == 0)
        {
            RpcErrors.RpcErrors401.AuthKeyUnregistered.ThrowRpcError();
        }

        var exceptAuthKeys = new HashSet<long>(obj.ExceptAuthKeys);
        var deviceReadModelList = await queryProcessor.ProcessAsync(new GetDeviceByUserIdQuery(input.UserId));

        foreach (var deviceReadModel in deviceReadModelList)
        {
            if (deviceReadModel.TempAuthKeyId == 0 || exceptAuthKeys.Contains(deviceReadModel.TempAuthKeyId))
            {
                continue;
            }

            var command = new UnRegisterDeviceForAuthKeyCommand(
                DeviceId.Create(deviceReadModel.PermAuthKeyId),
                deviceReadModel.PermAuthKeyId,
                deviceReadModel.TempAuthKeyId);
            await commandBus.PublishAsync(command);

            await eventBus.PublishAsync(new AuthKeyUnRegisteredIntegrationEvent(
                deviceReadModel.PermAuthKeyId,
                deviceReadModel.TempAuthKeyId));
        }

        return new TBoolTrue();
    }
}
