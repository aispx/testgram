using MongoDB.Driver;
using MongoDB.Bson;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;

/// <summary>
/// Get the admin log of a <a href="https://corefork.telegram.org/api/channel">channel/supergroup</a>
/// See <a href="https://corefork.telegram.org/method/channels.getAdminLog"/>
/// </summary>
internal sealed class GetAdminLogHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IUserConverterService userConverterService,
    IChannelAppService channelAppService,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    IChatConverterService chatConverterService)
    : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestGetAdminLog, MyTelegram.Schema.Channels.IAdminLogResults>
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 100;

    /// <summary>
    /// How many users a search query may resolve to. The query string is also matched against participant
    /// names, and without a cap a single letter would pull in the whole user base.
    /// </summary>
    private const int MaxSearchUsers = 100;

    protected override async Task<MyTelegram.Schema.Channels.IAdminLogResults> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Channels.RequestGetAdminLog obj)
    {
        // GetChannel validates the access hash and also accepts inputChannelFromMessage.
        var peer = peerHelper.GetChannel(obj.Channel);

        if (peer.PeerType != PeerType.Channel)
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
        }

        var channelReadModel = await channelAppService.GetAsync(peer.PeerId);
        if (channelReadModel == null)
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
        }

        if (!await channelAppService.IsChannelMemberAsync(input.UserId, peer.PeerId))
        {
            RpcErrors.RpcErrors406.ChannelPrivate.ThrowRpcError();
        }

        // Any admin right is enough to read the log; the creator holds them all.
        await channelAdminRightsChecker.CheckAdminRightAsync(peer.PeerId, input.UserId, _ => true,
            RpcErrors.RpcErrors403.ChatAdminRequired);

        var collection = mongoDatabase.GetCollection<BsonDocument>(AdminLogCollection.Name);

        var adminIds = obj.Admins is { Count: > 0 }
            ? obj.Admins.Select(a => peerHelper.GetPeer(a, input.UserId).PeerId).Distinct().ToList()
            : null;

        var queryUserIds = string.IsNullOrWhiteSpace(obj.Q) ? null : await SearchUserIdsAsync(obj.Q);

        var filter = AdminLogQuery.Build(
            peer.PeerId,
            obj.MaxId,
            obj.MinId,
            obj.EventsFilter == null ? null : AdminLogQuery.Tags(obj.EventsFilter),
            adminIds,
            obj.Q,
            queryUserIds);

        // Limit(0) means "no limit" in the Mongo driver, and each row deserializes an embedded TL
        // blob below, so an unclamped value lets one request load the whole log into memory.
        var limit = obj.Limit > 0 ? Math.Min(obj.Limit, MaxLimit) : DefaultLimit;

        var events = await collection
            .Find(filter)
            .SortByDescending(e => e["event_id"])
            .Limit(limit)
            .ToListAsync();

        var tlEvents = new TVector<IChannelAdminLogEvent>();
        var userIds = new HashSet<long>();
        var channelIds = new HashSet<long> { peer.PeerId };

        foreach (var evt in events)
        {
            var userId = evt["user_id"].ToInt64();
            userIds.Add(userId);
            CollectIds(evt, "related_user_ids", userIds);
            CollectIds(evt, "related_channel_ids", channelIds);

            var actionData = evt["action"]["data"].AsByteArray;
            var actionBuffer = new ReadOnlyMemory<byte>(actionData);
            var action = actionBuffer.Read<IChannelAdminLogEventAction>();

            tlEvents.Add(new TChannelAdminLogEvent
            {
                Id = evt["event_id"].ToInt64(),
                Date = evt["date"].BsonType == BsonType.DateTime
                    ? (int)new DateTimeOffset(evt["date"].ToUniversalTime()).ToUnixTimeSeconds()
                    : evt["date"].ToInt32(),
                UserId = userId,
                Action = action
            });
        }

        var users = new TVector<IUser>();
        if (userIds.Count > 0)
        {
            var userList = await userConverterService.GetUserListAsync(input, userIds.ToList(), false, false, input.Layer);
            users = new TVector<IUser>(userList.Cast<IUser>());
        }

        // The channel itself plus every channel referenced by an event (a linked discussion group, for
        // instance), otherwise the client cannot render those entries.
        var chats = new TVector<IChat>();
        var channelReadModels = await channelAppService.GetListAsync(channelIds);
        foreach (var readModel in channelReadModels)
        {
            chats.Add(chatConverterService.ToChannel(input, readModel, null, null, false, input.Layer));
        }

        return new TAdminLogResults
        {
            Events = tlEvents,
            Chats = chats,
            Users = users
        };
    }

    /// <summary>
    /// The users whose name or username matches the search query, so that an event about a participant is
    /// found by that participant's name and not only by the text stored with the event.
    /// </summary>
    private async Task<List<long>> SearchUserIdsAsync(string query)
    {
        var userCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel");
        var f = Builders<BsonDocument>.Filter;

        // Escaped: an unescaped client regex is evaluated against every user document and can be made
        // to backtrack catastrophically.
        var pattern = new BsonRegularExpression(Regex.Escape(query.Trim()), "i");

        var users = await userCollection
            .Find(f.Or(
                f.Regex("UserName", pattern),
                f.Regex("FirstName", pattern),
                f.Regex("LastName", pattern)))
            .Project(Builders<BsonDocument>.Projection.Include("UserId"))
            .Limit(MaxSearchUsers)
            .ToListAsync();

        return users
            .Where(u => u.Contains("UserId"))
            .Select(u => u["UserId"].ToInt64())
            .ToList();
    }

    private static void CollectIds(BsonDocument document, string field, HashSet<long> target)
    {
        if (document.GetValue(field, BsonNull.Value) is BsonArray array)
        {
            foreach (var value in array)
            {
                target.Add(value.ToInt64());
            }
        }
    }

}
