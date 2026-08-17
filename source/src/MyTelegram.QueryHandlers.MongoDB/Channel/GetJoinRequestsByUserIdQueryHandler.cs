namespace MyTelegram.QueryHandlers.MongoDB.Channel;

public class
    GetJoinRequestsByUserIdQueryHandler(IQueryOnlyReadModelStore<JoinChannelRequestReadModel> store) : IQueryHandler<GetJoinRequestsByUserIdQuery, IReadOnlyCollection<IJoinChannelRequestReadModel>>
{
    public async Task<IReadOnlyCollection<IJoinChannelRequestReadModel>> ExecuteQueryAsync(GetJoinRequestsByUserIdQuery query, CancellationToken cancellationToken)
    {
        var items = await store.FindAsync(p => p.UserId == query.UserId && p.Date >= query.MinDate,
            cancellationToken: cancellationToken);

        return items.OrderByDescending(p => p.Date).Take(query.Limit).ToList();
    }
}
