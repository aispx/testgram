namespace MyTelegram.QueryHandlers.MongoDB.Messaging;

public class GetPostsCountQueryHandler(IQueryOnlyReadModelStore<MessageReadModel> store,
    IQueryOnlyReadModelStore<MessageTokenReadModel> messageTokenStore)
    : IQueryHandler<GetPostsCountQuery, int>
{
    private const int MinTextSearchLength = 2;

    public async Task<int> ExecuteQueryAsync(GetPostsCountQuery query, CancellationToken cancellationToken)
    {
        var q = query.Query?.Trim() ?? string.Empty;
        if (query.Tokens?.Count > 0)
        {
            Expression<Func<MessageTokenReadModel, bool>> tokenPredicate = p => p.PublicPosts && p.Date > query.OffsetRate && p.MessageId > query.OffsetId;
            tokenPredicate = tokenPredicate.WhereIf(!string.IsNullOrEmpty(query.Hashtag), p => p.Hashtags.Contains(query.Hashtag))
                .WhereIf(query.Tokens.Count > 0, p => query.Tokens.Any(x => p.Tokens.Contains(x)))
                .WhereIf(query.OffsetPeerId != 0, p => p.OwnerPeerId == query.OffsetPeerId);

            return (int)await messageTokenStore.CountAsync(tokenPredicate, cancellationToken);
        }

        Expression<Func<MessageReadModel, bool>> predicate = p => p.PublicPosts && p.Date > query.OffsetRate && p.MessageId > query.OffsetId;
        predicate = predicate.WhereIf(!string.IsNullOrEmpty(query.Hashtag), p => p.Hashtags.Contains(query.Hashtag))
            .WhereIf(q.Length >= MinTextSearchLength, p => p.Message.Contains(q))
            .WhereIf(query.OffsetPeerId != 0, p => p.ToPeerId == query.OffsetPeerId || p.OwnerPeerId == query.OffsetPeerId);

        return (int)await store.CountAsync(predicate, cancellationToken: cancellationToken);
    }
}
