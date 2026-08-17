namespace MyTelegram.QueryHandlers.InMemory.ChatInvite;

public class
    GetChatInvitesQueryHandler(IQueryOnlyReadModelStore<ChatInviteReadModel> store) : IQueryHandler<GetChatInvitesQuery, IReadOnlyCollection<IChatInviteReadModel>>
{
    public async Task<IReadOnlyCollection<IChatInviteReadModel>> ExecuteQueryAsync(GetChatInvitesQuery query,
        CancellationToken cancellationToken)
    {
        var items = await store.FindAsync(
            ChatInvitePredicateBuilder.Build(query.Revoked, query.PeerId, query.AdminId, query.OffsetDate),
            cancellationToken: cancellationToken);

        return ChatInvitePredicateBuilder.Page(items, query.OffsetDate, query.OffsetLink, query.Limit);
    }
}

public class
    GetChatInvitesCountQueryHandler(IQueryOnlyReadModelStore<ChatInviteReadModel> store) : IQueryHandler<GetChatInvitesCountQuery, int>
{
    public async Task<int> ExecuteQueryAsync(GetChatInvitesCountQuery query, CancellationToken cancellationToken)
    {
        var items = await store.FindAsync(
            ChatInvitePredicateBuilder.Build(query.Revoked, query.PeerId, query.AdminId, null),
            cancellationToken: cancellationToken);

        return items.Count;
    }
}

internal static class ChatInvitePredicateBuilder
{
    public static Expression<Func<ChatInviteReadModel, bool>> Build(bool revoked, long peerId, long adminId, int? offsetDate)
    {
        Expression<Func<ChatInviteReadModel, bool>> predicate = p =>
            p.Revoked == revoked &&
            p.PeerId == peerId &&
            p.AdminId == adminId;

        // Invite links are listed newest first, so offset_date is the date of the last item of the
        // previous page and acts as an inclusive upper bound; links sharing that date are then
        // separated by offset_link.
        return predicate.WhereIf(offsetDate is > 0, p => p.Date <= offsetDate);
    }

    /// <summary>
    /// Applies the newest-first ordering and the (offset_date, offset_link) cursor. The read model
    /// store cannot express "same date but after this link", so the tie-break runs in memory over
    /// the already date-bounded result.
    /// </summary>
    public static IReadOnlyCollection<IChatInviteReadModel> Page(IReadOnlyCollection<ChatInviteReadModel> items,
        int? offsetDate,
        string offsetLink,
        int limit)
    {
        var ordered = items
            .OrderByDescending(p => p.Date)
            .ThenBy(p => p.Link, StringComparer.Ordinal)
            .ToList();

        var cursor = string.IsNullOrEmpty(offsetLink)
            ? -1
            : ordered.FindIndex(p => p.Date == offsetDate && p.Link == offsetLink);

        IEnumerable<ChatInviteReadModel> page = ordered;
        if (cursor >= 0)
        {
            page = ordered.Skip(cursor + 1);
        }
        else if (offsetDate is > 0)
        {
            // No offset link, or the link it pointed at is gone (revoked or deleted between
            // pages): page by date alone rather than returning nothing.
            page = ordered.Where(p => p.Date < offsetDate);
        }

        if (limit > 0)
        {
            page = page.Take(limit);
        }

        return page.ToList();
    }
}
