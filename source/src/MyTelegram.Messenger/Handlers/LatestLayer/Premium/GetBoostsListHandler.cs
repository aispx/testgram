using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Premium;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Premium;
/// <summary>
/// Obtains info about the boosts that were applied to a certain channel or supergroup (admins only)
/// Possible errors
/// Code Type Description
/// 400 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/premium.getBoostsList"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetBoostsListHandler : RpcResultObjectHandler<MyTelegram.Schema.Premium.RequestGetBoostsList, MyTelegram.Schema.Premium.IBoostsList>
{
    private readonly IMongoDatabase _mongoDatabase;
    private readonly IPeerHelper _peerHelper;
    private readonly IUserAppService _userAppService;
    private readonly IUserConverterService _userConverterService;

    public GetBoostsListHandler(
        IMongoDatabase mongoDatabase,
        IPeerHelper peerHelper,
        IUserAppService userAppService,
        IUserConverterService userConverterService)
    {
        _mongoDatabase = mongoDatabase;
        _peerHelper = peerHelper;
        _userAppService = userAppService;
        _userConverterService = userConverterService;
    }

    protected override async Task<IBoostsList> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Premium.RequestGetBoostsList obj)
    {
        var peer = _peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null || peer.PeerType != PeerType.Channel)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var channelId = peer.PeerId;
        var collection = _mongoDatabase.GetCollection<BsonDocument>("channel_boosts");

        var filter = Builders<BsonDocument>.Filter.Eq("ChannelId", channelId);
        var boostDocs = await collection.Find(filter)
            .Sort(Builders<BsonDocument>.Sort.Descending("Date"))
            .ToListAsync();

        // Group boosts by UserId and sum multipliers
        var groupedBoosts = boostDocs
            .GroupBy(doc => doc["UserId"].BsonType == BsonType.Int64 ? doc["UserId"].AsInt64 : doc["UserId"].AsInt32)
            .Select(g => new
            {
                UserId = g.Key,
                TotalMultiplier = g.Sum(doc => doc.Contains("Multiplier") ? doc["Multiplier"].AsInt32 : 1),
                LatestDate = g.Max(doc => GetDateAsInt32(doc["Date"])),
                LatestExpires = g.Max(doc => GetDateAsInt32(doc["Expires"])),
                FirstId = g.First()["_id"].ToString(),
                Gift = g.Any(doc => doc.Contains("Gift") && doc["Gift"].AsBoolean),
                Giveaway = g.Any(doc => doc.Contains("Giveaway") && doc["Giveaway"].AsBoolean),
                Unclaimed = g.Any(doc => doc.Contains("Unclaimed") && doc["Unclaimed"].AsBoolean)
            })
            .OrderByDescending(x => x.LatestDate)
            .Take(obj.Limit)
            .ToList();

        var boosts = new List<IBoost>();
        var userIds = new HashSet<long>();

        foreach (var grouped in groupedBoosts)
        {
            userIds.Add(grouped.UserId);

            boosts.Add(new TBoost
            {
                Id = grouped.FirstId,
                UserId = grouped.UserId,
                Date = grouped.LatestDate,
                Expires = grouped.LatestExpires,
                Multiplier = grouped.TotalMultiplier,
                Gift = grouped.Gift,
                Giveaway = grouped.Giveaway,
                Unclaimed = grouped.Unclaimed
            });
        }

        var userReadModels = await _userAppService.GetListAsync(userIds.ToList());
        var users = userReadModels
            .Select(userReadModel => (IUser)_userConverterService.ToUser(input, userReadModel, layer: input.Layer))
            .ToList();

        return new TBoostsList
        {
            Count = groupedBoosts.Count,
            Boosts = new TVector<IBoost>(boosts),
            Users = new TVector<IUser>(users)
        };
    }

    private static int GetDateAsInt32(BsonValue value)
    {
        return value.BsonType switch
        {
            BsonType.Int32 => value.AsInt32,
            BsonType.Int64 => (int)value.AsInt64,
            BsonType.DateTime => (int)new DateTimeOffset(value.ToUniversalTime()).ToUnixTimeSeconds(),
            _ => 0
        };
    }
}
