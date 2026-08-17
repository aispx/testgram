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
    IQueryProcessor queryProcessor,
    IChatConverterService chatConverterService)
    : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestGetAdminLog, MyTelegram.Schema.Channels.IAdminLogResults>
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 100;

    protected override async Task<MyTelegram.Schema.Channels.IAdminLogResults> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Channels.RequestGetAdminLog obj)
    {
        // Validate channel access
        if (obj.Channel is not TInputChannel inputChannel)
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
            return null!;
        }

        var peer = peerHelper.GetPeer(inputChannel.ChannelId);

        if (peer.PeerType != PeerType.Channel)
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();

        // Check if user is admin
        var channelMemberCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-channelmemberreadmodel");
        var memberFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("ChannelId", peer.PeerId),
            Builders<BsonDocument>.Filter.Eq("UserId", input.UserId)
        );
        var member = await channelMemberCol.Find(memberFilter).FirstOrDefaultAsync();

        if (member == null)
            RpcErrors.RpcErrors406.ChannelPrivate.ThrowRpcError();

        // AdminRights is a non-nullable int on the read model, so an IsBsonNull test passes for
        // every member; and IsCreator is not persisted at all. Check the rights mask and the
        // IsAdmin flag that actually exist, otherwise any member can read the moderation history.
        var isAdmin = member.Contains("IsAdmin") && !member["IsAdmin"].IsBsonNull && member["IsAdmin"].AsBoolean;
        var adminRights = member.Contains("AdminRights") && !member["AdminRights"].IsBsonNull &&
                          member["AdminRights"].ToInt32() != 0;

        if (!isAdmin && !adminRights)
            RpcErrors.RpcErrors403.ChatAdminRequired.ThrowRpcError();

        // Build filter for admin log query
        var collection = mongoDatabase.GetCollection<BsonDocument>("channel_admin_log");
        var filterBuilder = Builders<BsonDocument>.Filter;
        var filters = new List<FilterDefinition<BsonDocument>>
        {
            filterBuilder.Eq("channel_id", peer.PeerId)
        };

        // Apply event ID range
        if (obj.MaxId > 0)
            filters.Add(filterBuilder.Lte("event_id", obj.MaxId));
        if (obj.MinId > 0)
            filters.Add(filterBuilder.Gte("event_id", obj.MinId));

        // Apply search query
        if (!string.IsNullOrEmpty(obj.Q))
        {
            // Search in action data (simplified - would need full-text search in production).
            // Escape the client string: an unescaped regex is evaluated server-side against every
            // document in the collection and can be made to backtrack catastrophically.
            filters.Add(filterBuilder.Regex("action.type",
                new BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(obj.Q), "i")));
        }

        // Apply admin filter
        if (obj.Admins != null && obj.Admins.Count > 0)
        {
            var adminIds = obj.Admins
                .Where(a => a is TInputUser)
                .Select(a => ((TInputUser)a).UserId)
                .ToList();

            if (adminIds.Count > 0)
                filters.Add(filterBuilder.In("user_id", adminIds));
        }

        // Apply events filter
        if (obj.EventsFilter != null)
        {
            var eventTypes = new List<string>();
            var filter = obj.EventsFilter;

            // The join filter also covers joins through an invite link and requests let through by
            // an admin, see https://corefork.telegram.org/constructor/channelAdminLogEventsFilter
            if (filter.Join)
            {
                eventTypes.Add("TChannelAdminLogEventActionParticipantJoin");
                eventTypes.Add("TChannelAdminLogEventActionParticipantJoinByInvite");
                eventTypes.Add("TChannelAdminLogEventActionParticipantJoinByRequest");
            }
            if (filter.Leave) eventTypes.Add("TChannelAdminLogEventActionParticipantLeave");
            if (filter.Invite) eventTypes.Add("TChannelAdminLogEventActionParticipantInvite");
            if (filter.Ban) eventTypes.Add("TChannelAdminLogEventActionParticipantToggleBan");
            if (filter.Promote) eventTypes.Add("TChannelAdminLogEventActionParticipantToggleAdmin");
            if (filter.Info)
            {
                eventTypes.Add("TChannelAdminLogEventActionChangeTitle");
                eventTypes.Add("TChannelAdminLogEventActionChangeAbout");
                eventTypes.Add("TChannelAdminLogEventActionChangeUsername");
                eventTypes.Add("TChannelAdminLogEventActionChangePhoto");
                eventTypes.Add("TChannelAdminLogEventActionChangePeerColor");
                eventTypes.Add("TChannelAdminLogEventActionChangeProfilePeerColor");
            }
            if (filter.Settings)
            {
                eventTypes.Add("TChannelAdminLogEventActionToggleInvites");
                eventTypes.Add("TChannelAdminLogEventActionToggleSignatures");
                eventTypes.Add("TChannelAdminLogEventActionTogglePreHistoryHidden");
            }
            if (filter.Pinned) eventTypes.Add("TChannelAdminLogEventActionUpdatePinned");
            if (filter.Edit) eventTypes.Add("TChannelAdminLogEventActionEditMessage");
            if (filter.Delete) eventTypes.Add("TChannelAdminLogEventActionDeleteMessage");
            if (filter.GroupCall)
            {
                eventTypes.Add("TChannelAdminLogEventActionStartGroupCall");
                eventTypes.Add("TChannelAdminLogEventActionDiscardGroupCall");
                eventTypes.Add("TChannelAdminLogEventActionToggleGroupCallSetting");
            }

            if (eventTypes.Count > 0)
                filters.Add(filterBuilder.In("action.type", eventTypes));
        }

        var combinedFilter = filterBuilder.And(filters);

        // Limit(0) means "no limit" in the Mongo driver, and each row deserializes an embedded TL
        // blob below, so an unclamped value lets one request load the whole log into memory.
        var limit = obj.Limit > 0 ? Math.Min(obj.Limit, MaxLimit) : DefaultLimit;

        // Execute query with pagination
        var events = await collection
            .Find(combinedFilter)
            .SortByDescending(e => e["date"])
            .ThenByDescending(e => e["event_id"])
            .Limit(limit)
            .ToListAsync();

        // Convert to TL objects
        var tlEvents = new TVector<IChannelAdminLogEvent>();
        var userIds = new HashSet<long>();
        var users = new TVector<IUser>();

        foreach (var evt in events)
        {
            var userId = evt["user_id"].AsInt64;
            userIds.Add(userId);

            // Deserialize action
            var actionData = evt["action"]["data"].AsByteArray;
            var actionBuffer = new ReadOnlyMemory<byte>(actionData);
            var action = actionBuffer.Read<IChannelAdminLogEventAction>();

            tlEvents.Add(new TChannelAdminLogEvent
            {
                Id = evt["event_id"].AsInt64,
                Date = evt["date"].BsonType == BsonType.DateTime
                    ? (int)new DateTimeOffset(evt["date"].ToUniversalTime()).ToUnixTimeSeconds()
                    : evt["date"].AsInt32,
                UserId = userId,
                Action = action
            });
        }

        // Load users
        if (userIds.Count > 0)
        {
            var userList = await userConverterService.GetUserListAsync(input, userIds.ToList(), false, false, input.Layer);
            users = new TVector<IUser>(userList.Cast<IUser>());
        }

        // Load channel
        var channelReadModel = await queryProcessor.ProcessAsync(
            new GetChannelByIdQuery(peer.PeerId));

        var chats = new TVector<IChat>();
        if (channelReadModel != null)
        {
            var channel = chatConverterService.ToChannel(input, channelReadModel, null, null, false, input.Layer);
            chats.Add(channel);
        }

        return new TAdminLogResults
        {
            Events = tlEvents,
            Chats = chats,
            Users = users
        };
    }
}
