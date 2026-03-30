using MyTelegram.ReadModel.MongoDB;

namespace MyTelegram.QueryHandlers.MongoDB.Messaging;

public class GetMessagesWithUnreadMentionsQueryHandler(IQueryOnlyReadModelStore<MessageReadModel> store)
    : IQueryHandler<GetMessagesWithUnreadMentionsQuery, IReadOnlyCollection<IMessageReadModel>>
{
    public async Task<IReadOnlyCollection<IMessageReadModel>> ExecuteQueryAsync(
        GetMessagesWithUnreadMentionsQuery query, CancellationToken cancellationToken)
    {
        return await store.FindAsync(
            m => m.OwnerPeerId == query.OwnerPeerId &&
                 m.MentionedUserIds != null &&
                 m.MentionedUserIds.Contains(query.UserId) &&
                 (query.OffsetId == 0 || m.MessageId < query.OffsetId) &&
                 (query.MaxId == 0 || m.MessageId <= query.MaxId) &&
                 (query.MinId == 0 || m.MessageId >= query.MinId),
            0,
            query.Limit,
            cancellationToken: cancellationToken);
    }
}
