namespace MyTelegram.QueryHandlers.MongoDB.Dialog;

public class GetMonoforumDialogsQueryHandler(IQueryOnlyReadModelStore<DialogReadModel> store)
    : IQueryHandler<GetMonoforumDialogsQuery, IReadOnlyCollection<IDialogReadModel>>
{
    public async Task<IReadOnlyCollection<IDialogReadModel>> ExecuteQueryAsync(GetMonoforumDialogsQuery query, CancellationToken cancellationToken)
    {
        Expression<Func<DialogReadModel, bool>> predicate = x =>
            x.ToPeerId == query.MonoforumChannelId &&
            x.ToPeerType == PeerType.Channel &&
            !x.IsDeleted;

        var sort = new SortOptions<DialogReadModel>(p => p.TopMessage, SortType.Descending);
        return await store.FindAsync(predicate, limit: query.Limit, sort: sort, cancellationToken: cancellationToken);
    }
}
