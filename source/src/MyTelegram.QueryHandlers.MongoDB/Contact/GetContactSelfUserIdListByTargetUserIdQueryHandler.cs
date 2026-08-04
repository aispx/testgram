namespace MyTelegram.QueryHandlers.MongoDB.Contact;

/// <inheritdoc cref="GetContactSelfUserIdListByTargetUserIdQuery"/>
public class GetContactSelfUserIdListByTargetUserIdQueryHandler(IQueryOnlyReadModelStore<ContactReadModel> store)
    : IQueryHandler<GetContactSelfUserIdListByTargetUserIdQuery, IReadOnlyCollection<long>>
{
    public async Task<IReadOnlyCollection<long>> ExecuteQueryAsync(
        GetContactSelfUserIdListByTargetUserIdQuery query,
        CancellationToken cancellationToken)
    {
        return await store.FindAsync(p => p.TargetUserId == query.TargetUserId,
            createResult: p => p.SelfUserId,
            cancellationToken: cancellationToken);
    }
}
