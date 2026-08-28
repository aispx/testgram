namespace MyTelegram.QueryHandlers.MongoDB.Dialog;

public class GetDialogsQueryHandler(IQueryOnlyReadModelStore<DialogReadModel> store) : IQueryHandler<GetDialogsQuery, IReadOnlyCollection<IDialogReadModel>>
{
    public async Task<IReadOnlyCollection<IDialogReadModel>> ExecuteQueryAsync(GetDialogsQuery query,
        CancellationToken cancellationToken)
    {
        // Fix native aot mission metadata issues
        var needPinnedParameter = false;
        var needOffsetDate = false;
        var pinned = false;
        var offsetDate = DateTime.UtcNow;
        if (query.Pinned.HasValue)
        {
            needPinnedParameter = true;
            pinned = query.Pinned.Value;
        }

        if (query.OffsetDate.HasValue)
        {
            needOffsetDate = true;
            offsetDate = query.OffsetDate.Value;
        }

        Expression<Func<DialogReadModel, bool>> predicate = x => x.OwnerId == query.OwnerId && !x.IsDeleted;
        // An absent folder_id means the main list, exactly as folder_id = 0 does (measured against the live
        // service: both answers are identical and archived chats appear only under folder_id = 1). A dialog
        // that was never archived carries no FolderId at all, so the main list has to accept null as well —
        // without that, archiving a chat left it in both lists.
        var mainList = !query.FolderId.HasValue || query.FolderId == 0;
        predicate = predicate
                .WhereIf(needOffsetDate, p => p.CreationTime > offsetDate)
                .WhereIf(needPinnedParameter, p => p.Pinned == pinned)
                .WhereIf(query.PeerIdList?.Count > 0, p => query.PeerIdList!.Contains(p.ToPeerId))
                .WhereIf(mainList, p => p.FolderId == null || p.FolderId == 0)
                .WhereIf(!mainList, p => p.FolderId == query.FolderId)
            ;

        var sort = new SortOptions<DialogReadModel>(p => p.TopMessage, SortType.Descending);
        return await store.FindAsync(predicate, limit: query.Limit, sort: sort, cancellationToken: cancellationToken);
    }
}
