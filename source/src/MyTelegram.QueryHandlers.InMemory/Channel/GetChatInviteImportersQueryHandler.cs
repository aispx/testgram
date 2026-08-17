namespace MyTelegram.QueryHandlers.InMemory.Channel;

public class
    GetChatInviteImportersQueryHandler(IQueryOnlyReadModelStore<JoinChannelRequestReadModel> store) : IQueryHandler<GetChatInviteImportersQuery, IReadOnlyCollection<IJoinChannelRequestReadModel>>
{
    public async Task<IReadOnlyCollection<IJoinChannelRequestReadModel>> ExecuteQueryAsync(GetChatInviteImportersQuery query, CancellationToken cancellationToken)
    {
        var predicate = JoinChannelRequestPredicateBuilder.Build(query.ChannelId,
            query.ChatInviteRequestState,
            query.InviteId,
            query.OffsetDate,
            query.OffsetUserId,
            query.UserIds);

        return await store.FindAsync(predicate, limit: query.Limit, cancellationToken: cancellationToken);
    }
}

public class
    GetChatInviteRequestCountQueryHandler(IQueryOnlyReadModelStore<JoinChannelRequestReadModel> store) : IQueryHandler<GetChatInviteRequestCountQuery, int>
{
    public async Task<int> ExecuteQueryAsync(GetChatInviteRequestCountQuery query, CancellationToken cancellationToken)
    {
        var predicate = JoinChannelRequestPredicateBuilder.Build(query.ChannelId,
            ChatInviteRequestState.WaitingForApproval,
            query.InviteId,
            null,
            null,
            query.UserIds);

        var items = await store.FindAsync(predicate, cancellationToken: cancellationToken);

        return items.Count;
    }
}

internal static class JoinChannelRequestPredicateBuilder
{
    public static Expression<Func<JoinChannelRequestReadModel, bool>> Build(long channelId,
        ChatInviteRequestState? chatInviteRequestState,
        long? inviteId,
        int? offsetDate,
        long? offsetUserId,
        List<long>? userIds)
    {
        Expression<Func<JoinChannelRequestReadModel, bool>> predicate = x => x.ChannelId == channelId;

        return predicate
            .WhereIf(chatInviteRequestState == ChatInviteRequestState.WaitingForApproval,
                p => !p.IsJoinRequestProcessed)
            .WhereIf(inviteId > 0, p => p.InviteId == inviteId)
            // Join requests are listed newest first, so offset_date is the date of the last item
            // of the previous page and acts as an exclusive upper bound.
            .WhereIf(offsetDate is > 0, p => p.Date < offsetDate)
            .WhereIf(offsetUserId is > 0, p => p.UserId != offsetUserId)
            .WhereIf(userIds != null, p => userIds!.Contains(p.UserId));
    }
}
