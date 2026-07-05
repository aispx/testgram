namespace MyTelegram.QueryHandlers.MongoDB.Contact;

public class GetImportedContactsByUserIdQueryHandler(
    IQueryOnlyReadModelStore<MyTelegram.ReadModel.MongoDB.ImportedContactReadModel> store)
    : IQueryHandler<GetImportedContactsByUserIdQuery, IReadOnlyCollection<IImportedContactReadModel>>
{
    public async Task<IReadOnlyCollection<IImportedContactReadModel>> ExecuteQueryAsync(
        GetImportedContactsByUserIdQuery query,
        CancellationToken cancellationToken)
    {
        return await store.FindAsync(p => p.SelfUserId == query.UserId, cancellationToken: cancellationToken);
    }
}
