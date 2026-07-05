using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Join a bot's affiliate program, becoming an affiliate
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.connectStarRefBot"/> </c></para>
/// </summary>
internal sealed class ConnectStarRefBotHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IQueryProcessor queryProcessor,
    IUserConverterService userConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestConnectStarRefBot, MyTelegram.Schema.Payments.IConnectedStarRefBots>
{
    protected override async Task<MyTelegram.Schema.Payments.IConnectedStarRefBots> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestConnectStarRefBot obj)
    {
        var botInput = obj.Bot as TInputUser;
        if (botInput == null)
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();

        var programsCol = mongoDatabase.GetCollection<BsonDocument>("star_ref_programs");
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var programFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("bot_id", botInput.UserId),
            Builders<BsonDocument>.Filter.Or(
                Builders<BsonDocument>.Filter.Not(Builders<BsonDocument>.Filter.Exists("end_date")),
                Builders<BsonDocument>.Filter.Eq("end_date", BsonNull.Value),
                Builders<BsonDocument>.Filter.Gt("end_date", now)
            )
        );
        var program = await programsCol.Find(programFilter).FirstOrDefaultAsync();
        if (program == null)
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();

        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null)
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();

        var botUser = await queryProcessor.ProcessAsync(new GetUserByIdQuery(botInput.UserId));
        if (botUser == null || string.IsNullOrEmpty(botUser.UserName))
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();

        var linkId = Guid.NewGuid().ToString("N").Substring(0, 16);
        var url = $"https://t.me/{botUser.UserName}?start=_tgr_{linkId}";

        var connectionsCol = mongoDatabase.GetCollection<BsonDocument>("connected_star_ref_bots");
        var now2 = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await connectionsCol.ReplaceOneAsync(
            Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("peer_id", peer.PeerId),
                Builders<BsonDocument>.Filter.Eq("bot_id", botInput.UserId)
            ),
            new BsonDocument
            {
                ["_id"] = $"starref-{peer.PeerId}-{botInput.UserId}",
                ["peer_id"] = peer.PeerId,
                ["peer_type"] = (int)peer.PeerType,
                ["bot_id"] = botInput.UserId,
                ["url"] = url,
                ["link_id"] = linkId,
                ["date"] = now2,
                ["commission_permille"] = program["commission_permille"].AsInt32,
                ["duration_months"] = program.Contains("duration_months") ? program["duration_months"].AsInt32 : 0,
                ["participants"] = 0,
                ["revenue"] = 0L,
                ["revoked"] = false
            },
            new ReplaceOptions { IsUpsert = true }
        );

        var users = await queryProcessor.ProcessAsync(new GetUsersByUserIdListQuery(new List<long> { botInput.UserId }));

        return new MyTelegram.Schema.Payments.TConnectedStarRefBots
        {
            Count = 1,
            ConnectedBots = new TVector<IConnectedBotStarRef>
            {
                new TConnectedBotStarRef
                {
                    Url = url,
                    Date = now,
                    BotId = botInput.UserId,
                    CommissionPermille = program["commission_permille"].AsInt32,
                    DurationMonths = program.Contains("duration_months") ? program["duration_months"].AsInt32 : null,
                    Participants = 0,
                    Revenue = 0
                }
            },
            Users = new TVector<IUser>(users.Select(u => StarRefBotUserHelper.BuildBotUser(userConverterService, input, u)))
        };
    }
}
