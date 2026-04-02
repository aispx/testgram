namespace MyTelegram.QueryHandlers.MongoDB.UserName;

public class SearchUserNameQueryHandler(IQueryOnlyReadModelStore<UserNameReadModel> store) : IQueryHandler<SearchUserNameQuery, IReadOnlyCollection<IUserNameReadModel>>
{
    public async Task<IReadOnlyCollection<IUserNameReadModel>> ExecuteQueryAsync(SearchUserNameQuery query,
        CancellationToken cancellationToken)
    {
        var q = query.Keyword?.Trim();
        if (string.IsNullOrEmpty(q))
        {
            return Array.Empty<IUserNameReadModel>();
        }

        // Remove @ prefix if present
        if (q.StartsWith('@'))
        {
            q = q[1..];
        }

        var qLower = q.ToLowerInvariant();

        // Search for usernames that start with or contain the query
        var results = await store.FindAsync(
            p => p.UserName.ToLower().StartsWith(qLower) || p.UserName.ToLower().Contains(qLower),
            limit: 100,
            cancellationToken: cancellationToken);

        // Sort by relevance: exact match > starts with > contains
        return results
            .OrderByDescending(u => u.UserName.Equals(qLower, StringComparison.OrdinalIgnoreCase) ? 2 : 0)
            .ThenByDescending(u => u.UserName.StartsWith(qLower, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(u => u.UserName.Length)
            .ThenBy(u => u.UserName)
            .Take(50)
            .ToList();
    }
}