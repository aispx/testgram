using MyTelegram.ReadModel;

namespace MyTelegram.QueryHandlers.MongoDB.Device;

/// <summary>
/// Returns all registered push devices addressable to a recipient account, matching either the
/// device owner (<c>UserId</c>) or any of the device's multi-account identifiers (<c>OtherUids</c>).
/// Used by the push dispatcher to deliver <see href="https://corefork.telegram.org/api/push-updates">PUSH notifications</see>
/// to every account active on a client (multi-account routing).
/// </summary>
public class GetPushDevicesForRecipientQueryHandler(IQueryOnlyReadModelStore<PushDeviceReadModel> store)
    : IQueryHandler<GetPushDevicesForRecipientQuery, IReadOnlyCollection<IPushDeviceReadModel>>
{
    public async Task<IReadOnlyCollection<IPushDeviceReadModel>> ExecuteQueryAsync(
        GetPushDevicesForRecipientQuery query,
        CancellationToken cancellationToken)
    {
        var recipientUserId = query.RecipientUserId;
        return await store.FindAsync(
            p => p.UserId == recipientUserId || (p.OtherUids != null && p.OtherUids.Contains(recipientUserId)),
            cancellationToken: cancellationToken);
    }
}
