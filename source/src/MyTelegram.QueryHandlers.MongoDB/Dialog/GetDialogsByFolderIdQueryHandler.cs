namespace MyTelegram.QueryHandlers.MongoDB.Dialog;

public class GetDialogsByFolderIdQueryHandler(IQueryOnlyReadModelStore<DialogReadModel> store)
    : IQueryHandler<GetDialogsByFolderIdQuery, IReadOnlyCollection<Peer>>
{
    public async Task<IReadOnlyCollection<Peer>> ExecuteQueryAsync(GetDialogsByFolderIdQuery query, CancellationToken cancellationToken)
    {
        // Folder 0 is the main list, and a dialog that was never archived carries no FolderId at all — asking
        // for folder 0 with an equality match found nothing.
        if (query.FolderId == 0)
        {
            return await store.FindAsync(
                p => p.OwnerId == query.OwnerUserId && !p.IsDeleted && (p.FolderId == null || p.FolderId == 0),
                p => new Peer(p.ToPeerType, p.ToPeerId), cancellationToken: cancellationToken);
        }

        return await store.FindAsync(
            p => p.OwnerId == query.OwnerUserId && !p.IsDeleted && p.FolderId == query.FolderId,
            p => new Peer(p.ToPeerType, p.ToPeerId), cancellationToken: cancellationToken);
    }
}