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

        var results = await store.FindAsync(p =>
            p.SelfUserId == query.SelfUserId && (
                // First name contains query
                p.FirstName.ToLower().Contains(qLower) ||
                // Last name contains query
                (p.LastName != null && p.LastName.ToLower().Contains(qLower)) ||
                // Full name contains query
                (p.LastName != null && (p.FirstName + " " + p.LastName).ToLower().Contains(qLower)) ||
                // Phone contains query
                (p.Phone != null && p.Phone.Contains(q))
            ),
            cancellationToken: cancellationToken);

        // Sort by relevance: starts with > contains
        return results
            .OrderByDescending(c => c.FirstName.StartsWith(qLower, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(c => c.FirstName)
            .ToList();
    }
}
