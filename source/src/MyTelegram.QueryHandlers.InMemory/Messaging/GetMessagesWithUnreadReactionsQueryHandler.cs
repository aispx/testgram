namespace MyTelegram.QueryHandlers.InMemory.Messaging;

public class GetMessagesWithUnreadReactionsQueryHandler(IQueryOnlyReadModelStore<MessageReadModel> store)
    : IQueryHandler<GetMessagesWithUnreadReactionsQuery, IReadOnlyCollection<IMessageReadModel>>
{
    public async Task<IReadOnlyCollection<IMessageReadModel>> ExecuteQueryAsync(
        GetMessagesWithUnreadReactionsQuery query, CancellationToken cancellationToken)
    {
        return await store.FindAsync(
            m => m.OwnerPeerId == query.OwnerPeerId &&
                 m.SenderUserId == query.SenderUserId &&
                 m.RecentReactions2 != null &&
                 m.RecentReactions2.Any(r => r.SenderUserId != query.SenderUserId && r.Date > query.ReadDate) &&
                 (!query.TopMsgId.HasValue || m.TopMsgId == query.TopMsgId.Value) &&
                 (query.SavedPeerId == null ||
                  m.SavedPeerId != null &&
                  m.SavedPeerId.PeerType == query.SavedPeerId.PeerType &&
                  m.SavedPeerId.PeerId == query.SavedPeerId.PeerId) &&
                 (query.OffsetId == 0 || m.MessageId < query.OffsetId) &&
                 (query.MaxId == 0 || m.MessageId <= query.MaxId) &&
                 (query.MinId == 0 || m.MessageId >= query.MinId),
            0,
            query.Limit,
            cancellationToken: cancellationToken);
    }
}
