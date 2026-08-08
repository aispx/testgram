using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Fetch all affiliations we have created for a certain peer
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.getConnectedStarRefBots"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetConnectedStarRefBotsHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IQueryProcessor queryProcessor,
    IUserConverterService userConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetConnectedStarRefBots, MyTelegram.Schema.Payments.IConnectedStarRefBots>
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 100;

    protected override async Task<MyTelegram.Schema.Payments.IConnectedStarRefBots> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetConnectedStarRefBots obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null)
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();

        var connectionsCol = mongoDatabase.GetCollection<BsonDocument>("connected_star_ref_bots");
        var filter = Builders<BsonDocument>.Filter.Eq("peer_id", peer.PeerId);

        // Apply cursor-based pagination
        if (obj.OffsetDate.HasValue && !string.IsNullOrEmpty(obj.OffsetLink))
        {
            filter = Builders<BsonDocument>.Filter.And(
                filter,
                Builders<BsonDocument>.Filter.Lt("date", obj.OffsetDate.Value)
            );
        }

        var query = connectionsCol.Find(filter).Sort(Builders<BsonDocument>.Sort.Descending("date"));

        // Limit(0) means "no limit" in the Mongo driver, so an unclamped value would return the
        // whole collection.
        var limit = obj.Limit > 0 ? Math.Min(obj.Limit, MaxLimit) : DefaultLimit;

        var connections = await query.Limit(limit).ToListAsync();

        if (connections.Count == 0)
        {
            return new MyTelegram.Schema.Payments.TConnectedStarRefBots
            {
                Count = 0,
                ConnectedBots = new TVector<IConnectedBotStarRef>(),
                Users = new TVector<IUser>()
            };
        }

        var botIds = connections.Select(c => c["bot_id"].AsInt64).Distinct().ToList();
        var users = await queryProcessor.ProcessAsync(new GetUsersByUserIdListQuery(botIds));

        var connectedBots = new TVector<IConnectedBotStarRef>();
        foreach (var conn in connections)
        {
            connectedBots.Add(new TConnectedBotStarRef
            {
                Revoked = conn["revoked"].AsBoolean,
                Url = conn["url"].AsString,
                Date = conn["date"].AsInt32,
                BotId = conn["bot_id"].AsInt64,
                CommissionPermille = conn["commission_permille"].AsInt32,
                DurationMonths = conn["duration_months"].AsInt32 > 0 ? conn["duration_months"].AsInt32 : null,
                Participants = conn["participants"].AsInt32,
                Revenue = conn["revenue"].AsInt64
            });
        }

        var userList = new TVector<IUser>();
        foreach (var user in users)
        {
            userList.Add(StarRefBotUserHelper.BuildBotUser(userConverterService, input, user));
        }

        return new MyTelegram.Schema.Payments.TConnectedStarRefBots
        {
            Count = connectedBots.Count,
            ConnectedBots = connectedBots,
            Users = userList
        };
    }
}
