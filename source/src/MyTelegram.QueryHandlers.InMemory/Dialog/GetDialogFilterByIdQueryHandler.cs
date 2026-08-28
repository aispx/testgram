namespace MyTelegram.QueryHandlers.InMemory.Dialog;

public class GetDialogFilterByIdQueryHandler(IQueryOnlyReadModelStore<DialogFilterReadModel> store)
    : IQueryHandler<GetDialogFilterByIdQuery, IDialogFilterReadModel?>
{
    public async Task<IDialogFilterReadModel?> ExecuteQueryAsync(GetDialogFilterByIdQuery query,
        CancellationToken cancellationToken)
    {
        return await store.FirstOrDefaultAsync(
            p => p.OwnerUserId == query.OwnerUserId && p.FolderId == query.FolderId, cancellationToken);
    }
}
