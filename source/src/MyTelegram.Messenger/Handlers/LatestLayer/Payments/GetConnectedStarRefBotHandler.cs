using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Fetch info about a specific bot affiliation
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.getConnectedStarRefBot"/> </c></para>
/// </summary>
internal sealed class GetConnectedStarRefBotHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetConnectedStarRefBot, MyTelegram.Schema.Payments.IConnectedStarRefBots>
{
    protected override async Task<MyTelegram.Schema.Payments.IConnectedStarRefBots> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetConnectedStarRefBot obj)
    {
        var botInput = obj.Bot as TInputUser;
        if (botInput == null)
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();

        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null)
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();

        var connectionsCol = mongoDatabase.GetCollection<BsonDocument>("connected_star_ref_bots");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("peer_id", peer.PeerId),
            Builders<BsonDocument>.Filter.Eq("bot_id", botInput.UserId)
        );

        var connection = await connectionsCol.Find(filter).FirstOrDefaultAsync();
        if (connection == null)
        {
            return new MyTelegram.Schema.Payments.TConnectedStarRefBots
            {
                Count = 0,
                ConnectedBots = new TVector<IConnectedBotStarRef>(),
                Users = new TVector<IUser>()
            };
        }

        var users = await queryProcessor.ProcessAsync(new GetUsersByUserIdListQuery(new List<long> { botInput.UserId }));

        return new MyTelegram.Schema.Payments.TConnectedStarRefBots
        {
            Count = 1,
            ConnectedBots = new TVector<IConnectedBotStarRef>
            {
                new TConnectedBotStarRef
                {
                    Url = connection["url"].AsString,
                    Date = connection["date"].AsInt32,
                    BotId = connection["bot_id"].AsInt64,
                    CommissionPermille = connection["commission_permille"].AsInt32,
                    DurationMonths = connection.Contains("duration_months") && connection["duration_months"].AsInt32 > 0
                        ? connection["duration_months"].AsInt32
                        : null,
                    Participants = connection["participants"].AsInt32,
                    Revenue = connection["revenue"].AsInt64,
                    Revoked = connection.Contains("revoked") && connection["revoked"].AsBoolean
                }
            },
            Users = new TVector<IUser>(users.Select(u => (IUser)new TUser
            {
                Id = u.UserId,
                AccessHash = u.AccessHash,
                FirstName = u.FirstName,
                LastName = u.LastName,
                Username = u.UserName,
                Phone = u.PhoneNumber,
                Photo = new TUserProfilePhotoEmpty(),
                Status = new TUserStatusEmpty(),
                Bot = true,
                RestrictionReason = new TVector<IRestrictionReason>()
            }))
        };
    }
}
