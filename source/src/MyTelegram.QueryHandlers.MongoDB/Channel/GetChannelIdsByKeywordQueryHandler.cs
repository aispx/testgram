namespace MyTelegram.QueryHandlers.MongoDB.Channel;

public class GetChannelIdsByKeywordQueryHandler(IQueryOnlyReadModelStore<ChannelReadModel> store)
    : IQueryHandler<GetChannelIdsByKeywordQuery, IReadOnlyCollection<long>>
{
    private const int MinKeywordLength = 2;
    private const int MaxLimit = 50;

    public async Task<IReadOnlyCollection<long>> ExecuteQueryAsync(GetChannelIdsByKeywordQuery query,
        CancellationToken cancellationToken)
    {
        var q = query.Keyword?.Trim();
        if (string.IsNullOrEmpty(q) || q.Length < MinKeywordLength)
        {
            return Array.Empty<long>();
        }

        var limit = query.Limit <= 0 ? 20 : Math.Min(query.Limit, MaxLimit);
        return await store.FindAsync(p => p.Title.Contains(q) &&
                                          (!string.IsNullOrEmpty(p.UserName) || p.CreatorId == query.UserId),
            createResult: p => p.ChannelId,
            limit: limit, cancellationToken: cancellationToken);
    }
}
