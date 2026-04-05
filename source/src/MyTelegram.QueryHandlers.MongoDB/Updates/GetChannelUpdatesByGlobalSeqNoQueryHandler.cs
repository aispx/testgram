namespace MyTelegram.QueryHandlers.MongoDB.Updates;

public class GetChannelUpdatesByGlobalSeqNoQueryHandler(IQueryOnlyReadModelStore<UpdatesReadModel> store) :
    IQueryHandler<GetChannelUpdatesByGlobalSeqNoQuery,
        IReadOnlyCollection<IUpdatesReadModel>>
{
    public async Task<IReadOnlyCollection<IUpdatesReadModel>> ExecuteQueryAsync(GetChannelUpdatesByGlobalSeqNoQuery query, CancellationToken cancellationToken)
    {
        return await store.FindAsync(p =>
            query.ChannelIdList.Contains(p.OwnerPeerId) &&
            p.GlobalSeqNo > query.MinGlobalSeqNo &&
            (p.OnlySendToUserId == null || p.OnlySendToUserId == query.SelfUserId) &&
            (p.ExcludeUserId == null || p.ExcludeUserId != query.SelfUserId),
            0, query.Limit, cancellationToken: cancellationToken);
    }
}