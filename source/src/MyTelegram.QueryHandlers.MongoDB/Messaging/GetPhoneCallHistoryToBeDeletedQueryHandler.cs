using MyTelegram;
namespace MyTelegram.QueryHandlers.MongoDB.Messaging;

public class GetPhoneCallHistoryToBeDeletedQueryHandler(IQueryOnlyReadModelStore<MessageReadModel> store)
    : IQueryHandler<GetPhoneCallHistoryToBeDeletedQuery, IReadOnlyCollection<MessageItemToBeDeleted>>
{
    public async Task<IReadOnlyCollection<MessageItemToBeDeleted>> ExecuteQueryAsync(
        GetPhoneCallHistoryToBeDeletedQuery query,
        CancellationToken cancellationToken)
    {
        var limit = query.Limit > 0 ? query.Limit : 100;
        var ownItems = await store.FindAsync(
            p => p.OwnerPeerId == query.UserId &&
                 (p.MessageType == MessageType.PhoneCall || p.MessageActionType == MessageActionType.PhoneCall),
            p => new MessageItemToBeDeleted(p.OwnerPeerId, p.MessageId, p.ToPeerType, p.ToPeerId),
            limit: limit,
            cancellationToken: cancellationToken);

        if (!query.Revoke || ownItems.Count == 0)
        {
            return ownItems;
        }

        var batchIds = (await store.FindAsync(
                p => p.OwnerPeerId == query.UserId &&
                     (p.MessageType == MessageType.PhoneCall || p.MessageActionType == MessageActionType.PhoneCall),
                p => p.BatchId,
                limit: limit,
                cancellationToken: cancellationToken))
            .Where(batchId => batchId != Guid.Empty)
            .Distinct()
            .ToList();
        if (batchIds.Count == 0)
        {
            return ownItems;
        }

        var revokedItems = await store.FindAsync(
            p => batchIds.Contains(p.BatchId),
            p => new MessageItemToBeDeleted(p.OwnerPeerId, p.MessageId, p.ToPeerType, p.ToPeerId),
            cancellationToken: cancellationToken);

        return ownItems
            .Concat(revokedItems)
            .GroupBy(item => new { item.OwnerUserId, item.MessageId })
            .Select(group => group.First())
            .ToList();
    }

}
