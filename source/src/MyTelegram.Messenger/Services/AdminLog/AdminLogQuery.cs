using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.AdminLog;

/// <summary>
/// Builds the MongoDB query behind
/// <a href="https://corefork.telegram.org/method/channels.getAdminLog">channels.getAdminLog</a>.
/// </summary>
public static class AdminLogQuery
{
    /// <summary>
    /// Maps the flags of
    /// <a href="https://corefork.telegram.org/constructor/channelAdminLogEventsFilter">channelAdminLogEventsFilter</a>
    /// onto the tags stored with every entry by <see cref="AdminLogMetadata"/>.
    /// </summary>
    public static List<string> Tags(IChannelAdminLogEventsFilter filter)
    {
        var tags = new List<string>();

        if (filter.Join) tags.Add(AdminLogMetadata.Join);
        if (filter.Leave) tags.Add(AdminLogMetadata.Leave);
        if (filter.Invite) tags.Add(AdminLogMetadata.Invite);
        if (filter.Ban) tags.Add(AdminLogMetadata.Ban);
        if (filter.Unban) tags.Add(AdminLogMetadata.Unban);
        if (filter.Kick) tags.Add(AdminLogMetadata.Kick);
        if (filter.Unkick) tags.Add(AdminLogMetadata.Unkick);
        if (filter.Promote) tags.Add(AdminLogMetadata.Promote);
        if (filter.Demote) tags.Add(AdminLogMetadata.Demote);
        if (filter.Info) tags.Add(AdminLogMetadata.Info);
        if (filter.Settings) tags.Add(AdminLogMetadata.Settings);
        if (filter.Pinned) tags.Add(AdminLogMetadata.Pinned);
        if (filter.Edit) tags.Add(AdminLogMetadata.Edit);
        if (filter.Delete) tags.Add(AdminLogMetadata.Delete);
        if (filter.GroupCall) tags.Add(AdminLogMetadata.GroupCall);
        if (filter.Invites) tags.Add(AdminLogMetadata.Invites);
        if (filter.Send) tags.Add(AdminLogMetadata.Send);
        if (filter.Forums) tags.Add(AdminLogMetadata.Forums);
        if (filter.SubExtend) tags.Add(AdminLogMetadata.SubExtend);
        if (filter.EditRank) tags.Add(AdminLogMetadata.EditRank);

        return tags;
    }

    /// <summary>
    /// <paramref name="maxId"/> and <paramref name="minId"/> are exclusive: clients paginate by passing
    /// the id of the oldest event they already hold as <c>max_id</c>, so an inclusive bound would hand
    /// them that event again on every page.
    /// </summary>
    public static FilterDefinition<BsonDocument> Build(
        long channelId,
        long maxId,
        long minId,
        IReadOnlyCollection<string>? tags,
        IReadOnlyCollection<long>? adminIds,
        string? query,
        IReadOnlyCollection<long>? queryUserIds)
    {
        var f = Builders<BsonDocument>.Filter;
        var conditions = new List<FilterDefinition<BsonDocument>>
        {
            f.Eq("channel_id", channelId)
        };

        if (maxId > 0)
        {
            conditions.Add(f.Lt("event_id", maxId));
        }

        if (minId > 0)
        {
            conditions.Add(f.Gt("event_id", minId));
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            conditions.Add(SearchCondition(query, queryUserIds));
        }

        if (adminIds is { Count: > 0 })
        {
            conditions.Add(f.In("user_id", adminIds));
        }

        // A filter with no flag set selects nothing, which is exactly what the client asked for.
        if (tags != null)
        {
            conditions.Add(f.In("filters", tags));
        }

        return f.And(conditions);
    }

    /// <summary>
    /// <c>q</c> matches the text carried by the event and the participants involved in it, which the
    /// caller resolves against the user read model so a later rename does not break the search.
    /// </summary>
    private static FilterDefinition<BsonDocument> SearchCondition(string query, IReadOnlyCollection<long>? queryUserIds)
    {
        var f = Builders<BsonDocument>.Filter;

        // The client string is escaped: an unescaped regex is evaluated server-side against every
        // document in the collection and can be made to backtrack catastrophically.
        var pattern = new BsonRegularExpression(Regex.Escape(query.Trim()), "i");

        var conditions = new List<FilterDefinition<BsonDocument>>
        {
            f.Regex("search_text", pattern)
        };

        if (queryUserIds is { Count: > 0 })
        {
            conditions.Add(f.In("user_id", queryUserIds));
            conditions.Add(f.In("related_user_ids", queryUserIds));
        }

        return f.Or(conditions);
    }
}
