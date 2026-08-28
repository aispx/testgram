namespace MyTelegram.QueryHandlers.MongoDB.Dialog;

/// <summary>
/// The folder a user imported from one <a href="https://corefork.telegram.org/api/links#chat-folder-links">chat
/// folder deep link</a>. The slug is the identity of an imported folder, not the exporter's filter id:
/// that number belongs to the exporter and collides with the importer's own folders.
/// </summary>
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
