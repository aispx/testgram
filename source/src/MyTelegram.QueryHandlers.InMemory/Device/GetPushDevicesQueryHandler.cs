using MyTelegram.ReadModel;

namespace MyTelegram.QueryHandlers.InMemory.Device;

/// <summary>
/// Returns all registered push devices for a user (FCM/APNS/APNS-VoIP/Web-Push tokens).
/// Used by the push dispatcher to deliver <see href="https://corefork.telegram.org/api/push-updates">PUSH notifications</see>.
/// </summary>
public class GetPushDevicesQueryHandler(IQueryOnlyReadModelStore<PushDeviceReadModel> store)
    : IQueryHandler<GetPushDevicesQuery, IReadOnlyCollection<IPushDeviceReadModel>>
{
    public async Task<IReadOnlyCollection<IPushDeviceReadModel>> ExecuteQueryAsync(
        GetPushDevicesQuery query,
        CancellationToken cancellationToken)
    {
        return await store.FindAsync(p => p.UserId == query.UserId, cancellationToken: cancellationToken);
    }
}
