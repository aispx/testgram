namespace MyTelegram.QueryHandlers.InMemory.Dialog;

public class GetImportedDialogFolderQueryHandler(IQueryOnlyReadModelStore<DialogFilterReadModel> store)
    : IQueryHandler<GetImportedDialogFolderQuery, IDialogFilterReadModel?>
{
    public async Task<IDialogFilterReadModel?> ExecuteQueryAsync(GetImportedDialogFolderQuery query,
        CancellationToken cancellationToken)
    {
        return await store.FirstOrDefaultAsync(
            p => p.OwnerUserId == query.UserId && p.ImportedFromSlug == query.Slug, cancellationToken);
    }
}
