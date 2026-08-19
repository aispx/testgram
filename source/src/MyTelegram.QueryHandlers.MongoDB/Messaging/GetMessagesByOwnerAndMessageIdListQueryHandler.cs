using MyTelegram.ReadModel.MongoDB;

namespace MyTelegram.QueryHandlers.MongoDB.Messaging;

public class GetMessagesByOwnerAndMessageIdListQueryHandler(IQueryOnlyReadModelStore<MessageReadModel> store)
    : IQueryHandler<GetMessagesByOwnerAndMessageIdListQuery, IReadOnlyCollection<IMessageReadModel>>
{
    public async Task<IReadOnlyCollection<IMessageReadModel>> ExecuteQueryAsync(
        GetMessagesByOwnerAndMessageIdListQuery query,
        CancellationToken cancellationToken)
    {
        return await store.FindAsync(
            p => p.OwnerPeerId == query.OwnerPeerId && query.MessageIdList.Contains(p.MessageId),
            cancellationToken: cancellationToken);
    }
}
