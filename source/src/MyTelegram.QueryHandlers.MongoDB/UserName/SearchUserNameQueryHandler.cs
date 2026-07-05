namespace MyTelegram.QueryHandlers.MongoDB.UserName;

public class SearchUserNameQueryHandler(IQueryOnlyReadModelStore<UserNameReadModel> store) : IQueryHandler<SearchUserNameQuery, IReadOnlyCollection<IUserNameReadModel>>
{
    private const int MinKeywordLength = 2;
    private const int MaxLimit = 50;

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
            q = q[1..].Trim();
        }

        if (q.Length < MinKeywordLength)
        {
            return Array.Empty<IUserNameReadModel>();
        }

        var qLower = q.ToLowerInvariant();

        // Use case-insensitive search without ToLower() in query
        // MongoDB will do case-sensitive comparison, so we search in memory after fetching
        var results = await store.FindAsync(
            p => p.UserName.StartsWith(q) ||
                 p.UserName.StartsWith(q.ToUpper()) ||
                 p.UserName.StartsWith(qLower) ||
                 p.UserName.Contains(q) ||
                 p.UserName.Contains(qLower),
            limit: MaxLimit,
            cancellationToken: cancellationToken);

        // Filter and sort by relevance in memory with case-insensitive comparison
        return results
            .Where(u => u.UserName.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(u => u.UserName.Equals(qLower, StringComparison.OrdinalIgnoreCase) ? 3 : 0)
            .ThenByDescending(u => u.UserName.StartsWith(qLower, StringComparison.OrdinalIgnoreCase) ? 2 : 0)
            .ThenByDescending(u => u.UserName.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(u => u.UserName.Length)
            .ThenBy(u => u.UserName)
            .Take(MaxLimit)
            .ToList();
    }
}
