using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Premium;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Premium;
/// <summary>
/// Returns the lists of boost that were applied to a channel/supergroup by a specific user (admins only)
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/premium.getUserBoosts"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class GetUserBoostsHandler : RpcResultObjectHandler<MyTelegram.Schema.Premium.RequestGetUserBoosts, MyTelegram.Schema.Premium.IBoostsList>
{
    private readonly IMongoDatabase _mongoDatabase;
    private readonly IPeerHelper _peerHelper;
    private readonly IUserAppService _userAppService;

    public GetUserBoostsHandler(
        IMongoDatabase mongoDatabase,
        IPeerHelper peerHelper,
        IUserAppService userAppService)
    {
        _mongoDatabase = mongoDatabase;
        _peerHelper = peerHelper;
        _userAppService = userAppService;
    }

    protected override async Task<IBoostsList> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Premium.RequestGetUserBoosts obj)
    {
        var peer = _peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null || peer.PeerType != PeerType.Channel)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var targetUserId = obj.UserId is TInputUser inputUser ? inputUser.UserId : 0;
        if (targetUserId == 0)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        var channelId = peer.PeerId;
        var collection = _mongoDatabase.GetCollection<BsonDocument>("channel_boosts");

        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("ChannelId", channelId),
            Builders<BsonDocument>.Filter.Eq("UserId", targetUserId)
        );

        var boostDocs = await collection.Find(filter)
            .Sort(Builders<BsonDocument>.Sort.Descending("Date"))
            .ToListAsync();

        var boosts = new List<IBoost>();

        foreach (var doc in boostDocs)
        {
            boosts.Add(new TBoost
            {
                Id = doc["_id"].AsString,
                UserId = targetUserId,
                Date = doc["Date"].AsInt32,
                Expires = doc["Expires"].AsInt32,
                Multiplier = doc.Contains("Multiplier") ? doc["Multiplier"].AsInt32 : 1,
                Gift = doc.Contains("Gift") && doc["Gift"].AsBoolean,
                Giveaway = doc.Contains("Giveaway") && doc["Giveaway"].AsBoolean,
                Unclaimed = doc.Contains("Unclaimed") && doc["Unclaimed"].AsBoolean
            });
        }

        var userReadModel = await _userAppService.GetAsync(targetUserId);
        var users = new List<IUser>();
        if (userReadModel != null)
        {
            users.Add(new TUser
            {
                Id = userReadModel.UserId,
                AccessHash = userReadModel.AccessHash,
                FirstName = userReadModel.FirstName ?? string.Empty,
                LastName = userReadModel.LastName,
                Username = userReadModel.UserName,
                Phone = userReadModel.PhoneNumber,
                Photo = new TUserProfilePhotoEmpty()
            });
        }

        return new TBoostsList
        {
            Count = boosts.Count,
            Boosts = new TVector<IBoost>(boosts),
            Users = new TVector<IUser>(users)
        };
    }
}