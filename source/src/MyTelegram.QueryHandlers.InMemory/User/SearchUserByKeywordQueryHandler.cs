namespace MyTelegram.QueryHandlers.InMemory.User;

public class SearchUserByKeywordQueryHandler(IQueryOnlyReadModelStore<UserReadModel> store) :
    IQueryHandler<SearchUserByKeywordQuery, IReadOnlyCollection<IUserReadModel>>
{
    private const int MinKeywordLength = 3;
    private const int MaxLimit = 50;

    public async Task<IReadOnlyCollection<IUserReadModel>> ExecuteQueryAsync(SearchUserByKeywordQuery query,
        CancellationToken cancellationToken)
    {
        var q = query.Keyword?.Trim();
        if (!string.IsNullOrEmpty(q) && q.StartsWith('@'))
        {
            q = q[1..].Trim();
        }

        if (string.IsNullOrEmpty(q) || q.Length < MinKeywordLength)
        {
            return Array.Empty<IUserReadModel>();
        }

        Expression<Func<UserReadModel, bool>> predicate =
            p => (p.UserName != null && p.UserName.StartsWith(q)) ||
                 p.FirstName.Contains(q) ||
                 (p.LastName != null && p.LastName.StartsWith(q));

        var limit = query.Limit <= 0 ? 20 : Math.Min(query.Limit, MaxLimit);
        return await store.FindAsync(predicate, 0, limit, new SortOptions<UserReadModel>(p => p.FirstName, SortType.Ascending), cancellationToken);
    }
}
