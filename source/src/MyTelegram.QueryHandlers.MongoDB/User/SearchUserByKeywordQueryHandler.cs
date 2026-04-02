namespace MyTelegram.QueryHandlers.MongoDB.User;

public class SearchUserByKeywordQueryHandler(IQueryOnlyReadModelStore<UserReadModel> store) :
    IQueryHandler<SearchUserByKeywordQuery, IReadOnlyCollection<IUserReadModel>>
{
    public async Task<IReadOnlyCollection<IUserReadModel>> ExecuteQueryAsync(SearchUserByKeywordQuery query,
        CancellationToken cancellationToken)
    {
        var q = query.Keyword?.Trim();
        if (string.IsNullOrEmpty(q))
        {
            return Array.Empty<IUserReadModel>();
        }

        // Remove @ prefix if present
        if (q.StartsWith('@'))
        {
            q = q[1..];
        }

        var qLower = q.ToLowerInvariant();

        Expression<Func<UserReadModel, bool>> predicate = p =>
            // Exact username match (highest priority)
            (p.UserName != null && p.UserName.ToLower() == qLower) ||
            // Username starts with query
            (p.UserName != null && p.UserName.ToLower().StartsWith(qLower)) ||
            // Username contains query
            (p.UserName != null && p.UserName.ToLower().Contains(qLower)) ||
            // First name starts with query
            p.FirstName.ToLower().StartsWith(qLower) ||
            // First name contains query
            p.FirstName.ToLower().Contains(qLower) ||
            // Last name starts with query
            (p.LastName != null && p.LastName.ToLower().StartsWith(qLower)) ||
            // Last name contains query
            (p.LastName != null && p.LastName.ToLower().Contains(qLower)) ||
            // Full name contains query (FirstName + LastName)
            (p.LastName != null && (p.FirstName + " " + p.LastName).ToLower().Contains(qLower)) ||
            // Phone contains query (for phone number search)
            (p.PhoneNumber != null && p.PhoneNumber.Contains(q));

        // Sort by relevance: exact match > starts with > contains
        var results = await store.FindAsync(predicate, 0, 100,
            new SortOptions<UserReadModel>(p => p.FirstName, SortType.Ascending),
            cancellationToken);

        // Re-sort by relevance in memory for better results
        return results
            .OrderByDescending(u => u.UserName?.Equals(qLower, StringComparison.OrdinalIgnoreCase) == true ? 3 : 0)
            .ThenByDescending(u => u.UserName?.StartsWith(qLower, StringComparison.OrdinalIgnoreCase) == true ? 2 : 0)
            .ThenByDescending(u => u.FirstName.StartsWith(qLower, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(u => u.FirstName)
            .Take(50)
            .ToList();
    }
}