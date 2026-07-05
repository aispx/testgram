using MyTelegram.Domain.Aggregates.Messaging;

namespace MyTelegram.QueryHandlers.MongoDB.Messaging;

public class SearchPostsQueryHandler(IQueryOnlyReadModelStore<MessageReadModel> store,
    IQueryOnlyReadModelStore<MessageTokenReadModel> messageTokenStore)
    : IQueryHandler<SearchPostsQuery, IReadOnlyCollection<IMessageReadModel>>
{
    private const int MinTextSearchLength = 2;

    public async Task<IReadOnlyCollection<IMessageReadModel>> ExecuteQueryAsync(SearchPostsQuery query, CancellationToken cancellationToken)
    {
        var q = query.Query?.Trim() ?? string.Empty;
        if (query.Tokens?.Count > 0)
        {
            return await SearchByTokensAsync(query, cancellationToken);
        }

        Expression<Func<MessageReadModel, bool>> predicate = p => p.PublicPosts && p.Date > query.OffsetRate && p.MessageId > query.OffsetId;
        predicate = predicate.WhereIf(!string.IsNullOrEmpty(query.Hashtag), p => p.Hashtags.Contains(query.Hashtag!))
            .WhereIf(q.Length >= MinTextSearchLength, p => p.Message.Contains(q))
            .WhereIf(query.OffsetPeerId != 0, p => p.ToPeerId == query.OffsetPeerId || p.OwnerPeerId == query.OffsetPeerId);

        return await store.FindAsync(predicate,
            limit: query.Limit,
            cancellationToken: cancellationToken);
    }

    private async Task<IReadOnlyCollection<IMessageReadModel>> SearchByTokensAsync(SearchPostsQuery query, CancellationToken cancellationToken)
    {
        Expression<Func<MessageTokenReadModel, bool>> predicate = p => p.PublicPosts && p.Date > query.OffsetRate && p.MessageId > query.OffsetId;
        predicate = predicate.WhereIf(!string.IsNullOrEmpty(query.Hashtag), p => p.Hashtags.Contains(query.Hashtag!))
            .WhereIf(query.Tokens?.Count > 0, p => query.Tokens!.Any(x => p.Tokens.Contains(x)))
            .WhereIf(query.OffsetPeerId != 0, p => p.OwnerPeerId == query.OffsetPeerId);

        var sortOptions = new SortOptions<MessageTokenReadModel>(p => p.MessageId, SortType.Descending);
        var result = await messageTokenStore.FindAsync(predicate,
            p => new TempMessageToken(p.OwnerPeerId, p.MessageId),
            0,
            query.Limit,
            sort: sortOptions,
            cancellationToken: cancellationToken);
        var messageTokenResult = result.OrderByDescending(p => p.MessageId).ToList();

        var messageIds = messageTokenResult.Select(p => MessageId.Create(p.OwnerPeerId, p.MessageId).Value);
     
        return await store.FindAsync(p => messageIds.Contains(p.Id), cancellationToken: cancellationToken);
    }
}
