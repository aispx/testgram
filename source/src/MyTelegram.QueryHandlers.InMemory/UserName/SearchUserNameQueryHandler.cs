namespace MyTelegram.QueryHandlers.InMemory.UserName;

public class SearchUserNameQueryHandler(IQueryOnlyReadModelStore<UserNameReadModel> store) : IQueryHandler<SearchUserNameQuery, IReadOnlyCollection<IUserNameReadModel>>
{
    private const int MinKeywordLength = 2;
    private const int MaxLimit = 50;

    public async Task<IReadOnlyCollection<IUserNameReadModel>> ExecuteQueryAsync(SearchUserNameQuery query,
        CancellationToken cancellationToken)
    {
        var q = query.Keyword?.Trim();
        if (!string.IsNullOrEmpty(q) && q.StartsWith('@'))
        {
            q = q[1..].Trim();
        }

        if (string.IsNullOrEmpty(q) || q.Length < MinKeywordLength)
        {
            return Array.Empty<IUserNameReadModel>();
        }

        return await store.FindAsync(p => p.UserName.StartsWith(q),
            limit: MaxLimit, cancellationToken: cancellationToken);
    }
}
