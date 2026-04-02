namespace MyTelegram.QueryHandlers.MongoDB.Contact;

public class SearchContactQueryHandler(IQueryOnlyReadModelStore<ContactReadModel> store) : IQueryHandler<SearchContactQuery, IReadOnlyCollection<IContactReadModel>>
{
    public async Task<IReadOnlyCollection<IContactReadModel>> ExecuteQueryAsync(SearchContactQuery query,
        CancellationToken cancellationToken)
    {
        var q = query.Keyword?.Trim();
        if (string.IsNullOrEmpty(q))
        {
            return Array.Empty<IContactReadModel>();
        }

        var qLower = q.ToLowerInvariant();
        var qUpper = q.ToUpperInvariant();

        // Use case-sensitive search in MongoDB
        var results = await store.FindAsync(p =>
            p.SelfUserId == query.SelfUserId && (
                // First name contains query
                p.FirstName.Contains(q) || p.FirstName.Contains(qLower) || p.FirstName.Contains(qUpper) ||
                // Last name contains query
                (p.LastName != null && (p.LastName.Contains(q) || p.LastName.Contains(qLower) || p.LastName.Contains(qUpper))) ||
                // Phone contains query
                (p.Phone != null && p.Phone.Contains(q))
            ),
            cancellationToken: cancellationToken);

        // Sort by relevance in memory with case-insensitive comparison
        return results
            .Where(c =>
                c.FirstName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (c.LastName != null && c.LastName.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                (c.Phone != null && c.Phone.Contains(q)))
            .OrderByDescending(c => c.FirstName.StartsWith(qLower, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(c => c.FirstName)
            .ToList();
    }
}
