namespace MyTelegram.QueryHandlers.InMemory.Contact;

public class SearchContactQueryHandler(IQueryOnlyReadModelStore<ContactReadModel> store) : IQueryHandler<SearchContactQuery, IReadOnlyCollection<IContactReadModel>>
{
    private const int MinKeywordLength = 2;
    private const int MaxLimit = 50;

    public async Task<IReadOnlyCollection<IContactReadModel>> ExecuteQueryAsync(SearchContactQuery query,
        CancellationToken cancellationToken)
    {
        var q = query.Keyword?.Trim();
        if (string.IsNullOrEmpty(q) || q.Length < MinKeywordLength)
        {
            return Array.Empty<IContactReadModel>();
        }

        var limit = query.Limit <= 0 ? 20 : Math.Min(query.Limit, MaxLimit);

        return await store.FindAsync(p =>
                    p.SelfUserId == query.SelfUserId &&
                    (p.FirstName.Contains(q) ||
                     (p.Phone != null && p.Phone.Contains(q)) ||
                     (p.LastName != null && p.LastName.Contains(q))),
                limit: limit,
                cancellationToken: cancellationToken);
    }
}
