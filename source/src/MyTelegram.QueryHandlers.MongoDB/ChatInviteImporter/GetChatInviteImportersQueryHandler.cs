namespace MyTelegram.QueryHandlers.MongoDB.ChatInviteImporter;

public class GetChatInviteImporterListQueryHandler(IQueryOnlyReadModelStore<ChatInviteImporterReadModel> store)
    : IQueryHandler<GetChatInviteImporterListQuery, IReadOnlyCollection<IChatInviteImporterReadModel>>
{
    public async Task<IReadOnlyCollection<IChatInviteImporterReadModel>> ExecuteQueryAsync(GetChatInviteImporterListQuery query, CancellationToken cancellationToken)
    {
        var predicate = ChatInviteImporterPredicateBuilder.Build(query.PeerId,
            query.InviteId,
            query.UserIds,
            query.SubscriptionExpired,
            query.OffsetDate,
            query.OffsetUserId);

        return await store.FindAsync(predicate, limit: query.Limit, cancellationToken: cancellationToken);
    }
}

public class GetChatInviteImporterCountQueryHandler(IQueryOnlyReadModelStore<ChatInviteImporterReadModel> store)
    : IQueryHandler<GetChatInviteImporterCountQuery, int>
{
    public async Task<int> ExecuteQueryAsync(GetChatInviteImporterCountQuery query, CancellationToken cancellationToken)
    {
        var predicate = ChatInviteImporterPredicateBuilder.Build(query.PeerId,
            query.InviteId,
            query.UserIds,
            query.SubscriptionExpired,
            null,
            null);

        var items = await store.FindAsync(predicate, cancellationToken: cancellationToken);

        return items.Count;
    }
}

internal static class ChatInviteImporterPredicateBuilder
{
    public static Expression<Func<ChatInviteImporterReadModel, bool>> Build(long peerId,
        long? inviteId,
        List<long>? userIds,
        bool subscriptionExpired,
        int? offsetDate,
        long? offsetUserId)
    {
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Only members that actually made it into the chat count as importers: a request that is
        // still waiting for approval, or that was rejected, never joined through the link.
        Expression<Func<ChatInviteImporterReadModel, bool>> predicate = x => x.PeerId == peerId &&
            (x.ChatInviteRequestState == ChatInviteRequestState.NoApprovalRequired ||
             x.ChatInviteRequestState == ChatInviteRequestState.Approved);

        return predicate
            .WhereIf(inviteId > 0, p => p.InviteId == inviteId)
            .WhereIf(userIds != null, p => userIds!.Contains(p.UserId))
            // subscription_expired lists members whose Star subscription has lapsed; without the
            // flag those members are hidden from the regular importer list.
            .WhereIf(subscriptionExpired, p => p.SubscriptionUntilDate != null && p.SubscriptionUntilDate < now)
            .WhereIf(!subscriptionExpired, p => p.SubscriptionUntilDate == null || p.SubscriptionUntilDate >= now)
            // Importers are listed newest first, so offset_date is an exclusive upper bound.
            .WhereIf(offsetDate is > 0, p => p.Date < offsetDate)
            .WhereIf(offsetUserId is > 0, p => p.UserId != offsetUserId);
    }
}
