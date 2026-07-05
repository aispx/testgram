namespace MyTelegram.QueryHandlers.MongoDB.User;

public class SearchUserByKeywordQueryHandler(IQueryOnlyReadModelStore<UserReadModel> store) :
    IQueryHandler<SearchUserByKeywordQuery, IReadOnlyCollection<IUserReadModel>>
{
    private const int MinKeywordLength = 3;
    private const int MaxLimit = 50;
    private const int MaxCandidateLimit = MaxLimit * 5;

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
            q = q[1..].Trim();
        }

        if (q.Length < MinKeywordLength)
        {
            return Array.Empty<IUserReadModel>();
        }

        var limit = query.Limit <= 0 ? 20 : Math.Min(query.Limit, MaxLimit);

        var qLower = q.ToLowerInvariant();
        var qUpper = q.ToUpperInvariant();

        // Use case-sensitive search in MongoDB, then filter in memory
        Expression<Func<UserReadModel, bool>> predicate = p =>
            // Username matches
            (p.UserName != null && (p.UserName.StartsWith(q) || p.UserName.StartsWith(qLower) || p.UserName.StartsWith(qUpper) || p.UserName.Contains(q) || p.UserName.Contains(qLower))) ||
            // First name matches
            (p.FirstName.StartsWith(q) || p.FirstName.StartsWith(qLower) || p.FirstName.StartsWith(qUpper) || p.FirstName.Contains(q) || p.FirstName.Contains(qLower)) ||
            // Last name matches
            (p.LastName != null && (p.LastName.StartsWith(q) || p.LastName.StartsWith(qLower) || p.LastName.StartsWith(qUpper) || p.LastName.Contains(q) || p.LastName.Contains(qLower))) ||
            // Phone contains query
            (p.PhoneNumber != null && p.PhoneNumber.Contains(q));

        var candidateLimit = Math.Min(Math.Max(limit * 5, limit), MaxCandidateLimit);
        var results = await store.FindAsync(predicate, 0, candidateLimit,
            new SortOptions<UserReadModel>(p => p.FirstName, SortType.Ascending),
            cancellationToken);

        // Re-sort by relevance in memory with proper case-insensitive comparison
        return results
            .Where(u =>
                (u.UserName != null && u.UserName.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                u.FirstName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (u.LastName != null && u.LastName.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                (u.PhoneNumber != null && u.PhoneNumber.Contains(q)))
            .OrderByDescending(u => u.UserName?.Equals(qLower, StringComparison.OrdinalIgnoreCase) == true ? 3 : 0)
            .ThenByDescending(u => u.UserName?.StartsWith(qLower, StringComparison.OrdinalIgnoreCase) == true ? 2 : 0)
            .ThenByDescending(u => u.FirstName.StartsWith(qLower, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(u => u.FirstName)
            .Take(limit)
            .ToList();
    }
}
