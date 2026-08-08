using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Leave a bot's <a href="https://corefork.telegram.org/api/bots/referrals#becoming-an-affiliate">affiliate program »</a>
/// Possible errors
/// Code Type Description
/// 400 STARREF_HASH_REVOKED The specified affiliate link was already revoked.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.editConnectedStarRefBot"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class EditConnectedStarRefBotHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IChannelAppService channelAppService,
    IQueryProcessor queryProcessor,
    IUserConverterService userConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestEditConnectedStarRefBot, MyTelegram.Schema.Payments.IConnectedStarRefBots>
{
    protected override async Task<MyTelegram.Schema.Payments.IConnectedStarRefBots> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestEditConnectedStarRefBot obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null)
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();

        // The affiliate url is public (it is handed out as a t.me referral link), so the peer is
        // the only thing binding a connection to its owner. GetPeer discards the access hash,
        // so without this check anyone could revoke another peer's referral income.
        if (peer.PeerType is PeerType.User or PeerType.Self)
        {
            if (peer.PeerId != input.UserId)
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }
        else if (peer.PeerType == PeerType.Channel)
        {
            var channel = await channelAppService.GetAsync(peer.PeerId);
            if (channel == null)
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            if (channel!.CreatorId != input.UserId)
                RpcErrors.RpcErrors403.ChatAdminRequired.ThrowRpcError();
        }
        else
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var connectionsCol = mongoDatabase.GetCollection<BsonDocument>("connected_star_ref_bots");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("peer_id", peer.PeerId),
            Builders<BsonDocument>.Filter.Eq("url", obj.Link)
        );

        var connection = await connectionsCol.Find(filter).FirstOrDefaultAsync();
        if (connection == null)
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();

        if (connection.Contains("revoked") && connection["revoked"].AsBoolean)
            RpcErrors.RpcErrors400.StarrefHashRevoked.ThrowRpcError();

        // Revoke the link
        if (obj.Revoked)
        {
            var update = Builders<BsonDocument>.Update.Set("revoked", true);
            await connectionsCol.UpdateOneAsync(filter, update);
        }

        // Return updated connection
        var updatedConnection = await connectionsCol.Find(filter).FirstOrDefaultAsync();
        var botId = updatedConnection["bot_id"].AsInt64;
        var users = await queryProcessor.ProcessAsync(new GetUsersByUserIdListQuery(new List<long> { botId }));

        var connectedBot = new TConnectedBotStarRef
        {
            Revoked = updatedConnection["revoked"].AsBoolean,
            Url = updatedConnection["url"].AsString,
            Date = updatedConnection["date"].AsInt32,
            BotId = botId,
            CommissionPermille = updatedConnection["commission_permille"].AsInt32,
            DurationMonths = updatedConnection["duration_months"].AsInt32 > 0 ? updatedConnection["duration_months"].AsInt32 : null,
            Participants = updatedConnection["participants"].AsInt32,
            Revenue = updatedConnection["revenue"].AsInt64
        };

        var userList = new TVector<IUser>();
        foreach (var user in users)
        {
            userList.Add(StarRefBotUserHelper.BuildBotUser(userConverterService, input, user));
        }

        return new MyTelegram.Schema.Payments.TConnectedStarRefBots
        {
            Count = 1,
            ConnectedBots = new TVector<IConnectedBotStarRef> { connectedBot },
            Users = userList
        };
    }
}
