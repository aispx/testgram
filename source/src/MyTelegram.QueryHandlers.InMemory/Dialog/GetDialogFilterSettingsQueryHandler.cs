namespace MyTelegram.QueryHandlers.InMemory.Dialog;

public class GetDialogFilterSettingsQueryHandler(IQueryOnlyReadModelStore<DialogFilterSettingsReadModel> store)
    : IQueryHandler<GetDialogFilterSettingsQuery, IDialogFilterSettingsReadModel?>
{
    public async Task<IDialogFilterSettingsReadModel?> ExecuteQueryAsync(GetDialogFilterSettingsQuery query,
        CancellationToken cancellationToken)
    {
        return await store.FirstOrDefaultAsync(p => p.OwnerUserId == query.OwnerUserId, cancellationToken);
    }
}
