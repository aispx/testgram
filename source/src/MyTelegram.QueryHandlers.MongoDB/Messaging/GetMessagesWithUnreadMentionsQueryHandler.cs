using System.Linq.Expressions;
using MyTelegram.ReadModel.MongoDB;

namespace MyTelegram.QueryHandlers.MongoDB.Messaging;

public class GetMessagesWithUnreadMentionsQueryHandler(IQueryOnlyReadModelStore<MessageReadModel> store)
    : IQueryHandler<GetMessagesWithUnreadMentionsQuery, IReadOnlyCollection<IMessageReadModel>>
{
    public async Task<IReadOnlyCollection<IMessageReadModel>> ExecuteQueryAsync(
        GetMessagesWithUnreadMentionsQuery query, CancellationToken cancellationToken)
    {
        // add_offset is how the official client walks back through the mention history: it asks for
        // limit=1 at add_offset=count-1 to land on the oldest unread mention first.
        var skip = query.AddOffset > 0 ? query.AddOffset : 0;

        return await store.FindAsync(
            UnreadMentionFilter.Build(query.OwnerPeerId,
                query.UserId,
                query.ToPeer,
                query.TopMsgId,
                query.ReadMaxId,
                query.ReadIds,
                query.OffsetId,
                query.MaxId,
                query.MinId),
            skip,
            query.Limit,
            new SortOptions<MessageReadModel>(p => p.MessageId, SortType.Descending),
            cancellationToken);
    }
}

public class GetUnreadMentionsCountQueryHandler(IQueryOnlyReadModelStore<MessageReadModel> store)
    : IQueryHandler<GetUnreadMentionsCountQuery, int>
{
    public async Task<int> ExecuteQueryAsync(GetUnreadMentionsCountQuery query, CancellationToken cancellationToken)
    {
        return (int)await store.CountAsync(
            UnreadMentionFilter.Build(query.OwnerPeerId,
                query.UserId,
                query.ToPeer,
                query.TopMsgId,
                query.ReadMaxId,
                query.ReadIds,
                0,
                0,
                0),
            cancellationToken);
    }
}

public class GetUnreadMentionIdListQueryHandler(IQueryOnlyReadModelStore<MessageReadModel> store)
    : IQueryHandler<GetUnreadMentionIdListQuery, IReadOnlyCollection<int>>
{
    public async Task<IReadOnlyCollection<int>> ExecuteQueryAsync(GetUnreadMentionIdListQuery query,
        CancellationToken cancellationToken)
    {
        return await store.FindAsync(
            UnreadMentionFilter.Build(query.OwnerPeerId,
                query.UserId,
                query.ToPeer,
                query.TopMsgId,
                query.ReadMaxId,
                query.ReadIds,
                0,
                0,
                0),
            p => p.MessageId,
            0,
            query.Limit,
            new SortOptions<MessageReadModel>(p => p.MessageId, SortType.Descending),
            cancellationToken);
    }
}

public class GetUnreadMentionCountByTopicQueryHandler(IQueryOnlyReadModelStore<MessageReadModel> store)
    : IQueryHandler<GetUnreadMentionCountByTopicQuery, IReadOnlyDictionary<int, int>>
{
    public async Task<IReadOnlyDictionary<int, int>> ExecuteQueryAsync(GetUnreadMentionCountByTopicQuery query,
        CancellationToken cancellationToken)
    {
        var channelId = query.ChannelId;
        var userId = query.UserId;
        var readMaxId = query.ReadMaxId;
        var readIds = query.ReadIds;

        var groups = await store.GroupByAsync(
            m => m.OwnerPeerId == channelId &&
                 m.MentionedUserIds != null &&
                 m.MentionedUserIds.Contains(userId) &&
                 m.MessageId > readMaxId &&
                 !readIds.Contains(m.MessageId) &&
                 m.TopMsgId != null,
            m => m.TopMsgId,
            g => new TopicMentionCount(g.Key, g.Count()));

        return groups
            .Where(p => p.TopMsgId.HasValue)
            .ToDictionary(p => p.TopMsgId!.Value, p => p.Count);
    }

    private record TopicMentionCount(int? TopMsgId, int Count);
}

internal static class UnreadMentionFilter
{
    public static Expression<Func<MessageReadModel, bool>> Build(
        long ownerPeerId,
        long userId,
        Peer toPeer,
        int? topMsgId,
        int readMaxId,
        IReadOnlyList<int> readIds,
        int offsetId,
        int maxId,
        int minId)
    {
        var toPeerType = toPeer.PeerType;
        var toPeerId = toPeer.PeerId;

        return m => m.OwnerPeerId == ownerPeerId &&
                    m.ToPeerType == toPeerType &&
                    m.ToPeerId == toPeerId &&
                    m.MentionedUserIds != null &&
                    m.MentionedUserIds.Contains(userId) &&
                    m.MessageId > readMaxId &&
                    !readIds.Contains(m.MessageId) &&
                    (!topMsgId.HasValue || m.TopMsgId == topMsgId.Value) &&
                    (offsetId == 0 || m.MessageId < offsetId) &&
                    (maxId == 0 || m.MessageId <= maxId) &&
                    (minId == 0 || m.MessageId >= minId);
    }
}
