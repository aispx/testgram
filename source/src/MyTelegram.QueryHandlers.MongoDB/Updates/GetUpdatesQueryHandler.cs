namespace MyTelegram.QueryHandlers.MongoDB.Updates;

public class GetUpdatesQueryHandler(IQueryOnlyReadModelStore<UpdatesReadModel> store) : IQueryHandler<GetUpdatesQuery, IReadOnlyCollection<IUpdatesReadModel>>
{
    public async Task<IReadOnlyCollection<IUpdatesReadModel>> ExecuteQueryAsync(GetUpdatesQuery query,
        CancellationToken cancellationToken)
    {
        Expression<Func<UpdatesReadModel, bool>> predicate = p => p.OwnerPeerId == query.PeerId &&
            (p.OnlySendToUserId == null || p.OnlySendToUserId == query.SelfUserId) &&
            (p.ExcludeUserId == null || p.ExcludeUserId != query.SelfUserId);
        predicate =
            predicate
            //.WhereIf(query.Date > 0, p => p.Date > query.Date)
            // MinPts is a lower bound, so it applies even at 0: rows carrying pts 0 sit outside the
            // pts box and replay through GlobalSeqNo instead. Skipping the filter entirely for
            // MinPts == 0 returned the whole box, truncated to a full page that the difference
            // converter then reports as a slice forever — a client with no state never converges.
            .And(p => p.Pts > query.MinPts);

        return await store.FindAsync(predicate,
            0,
            query.Limit,
            cancellationToken: cancellationToken);
    }
}