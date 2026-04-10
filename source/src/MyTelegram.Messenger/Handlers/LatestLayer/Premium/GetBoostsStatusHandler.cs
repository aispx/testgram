using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema.Premium;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Premium;
/// <summary>
/// Gets the current <a href="https://corefork.telegram.org/api/boost">number of boosts</a> of a channel/supergroup.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/premium.getBoostsStatus"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetBoostsStatusHandler : RpcResultObjectHandler<MyTelegram.Schema.Premium.RequestGetBoostsStatus, MyTelegram.Schema.Premium.IBoostsStatus>
{
    private readonly IMongoDatabase _mongoDatabase;
    private readonly IPeerHelper _peerHelper;

    public GetBoostsStatusHandler(
        IMongoDatabase mongoDatabase,
        IPeerHelper peerHelper)
    {
        _mongoDatabase = mongoDatabase;
        _peerHelper = peerHelper;
    }

    protected override async Task<IBoostsStatus> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Premium.RequestGetBoostsStatus obj)
    {
        var peer = _peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null || peer.PeerType != PeerType.Channel)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var channelId = peer.PeerId;

        var channelCol = _mongoDatabase.GetCollection<BsonDocument>("eventflow-channelreadmodel");
        var channelDoc = await channelCol.Find(Builders<BsonDocument>.Filter.Eq("ChannelId", channelId)).FirstOrDefaultAsync();

        string? username = null;
        if (channelDoc?.Contains("UserName") == true && !channelDoc["UserName"].IsBsonNull)
        {
            var usernameValue = channelDoc["UserName"].AsString;
            if (!string.IsNullOrEmpty(usernameValue))
            {
                username = usernameValue;
            }
        }

        var collection = _mongoDatabase.GetCollection<BsonDocument>("channel_boosts");

        var boosts = await collection.Find(Builders<BsonDocument>.Filter.Eq("ChannelId", channelId)).ToListAsync();
        var totalBoosts = boosts.Sum(b => b.Contains("Multiplier") ? b["Multiplier"].AsInt32 : 1);

        var level = CalculateLevel(totalBoosts);
        var currentLevelBoosts = GetBoostsForLevel(level);
        var nextLevelBoosts = GetBoostsForLevel(level + 1);

        // Filter out system boosts (UserId = 0) for user-specific checks
        var userBoostsOnly = boosts.Where(b =>
        {
            if (!b.Contains("UserId") || b["UserId"].IsBsonNull)
                return false;
            var userId = b["UserId"].BsonType == BsonType.Int64 ? b["UserId"].AsInt64 : b["UserId"].AsInt32;
            return userId != 0;
        }).ToList();

        var myBoost = userBoostsOnly.Any(b =>
        {
            var userId = b["UserId"].BsonType == BsonType.Int64 ? b["UserId"].AsInt64 : b["UserId"].AsInt32;
            return userId == input.UserId;
        });

        TVector<int>? myBoostSlots = null;
        if (myBoost)
        {
            var myBoosts = userBoostsOnly.Where(b =>
            {
                var userId = b["UserId"].BsonType == BsonType.Int64 ? b["UserId"].AsInt64 : b["UserId"].AsInt32;
                return userId == input.UserId;
            }).ToList();
            myBoostSlots = new TVector<int>();
            foreach (var boost in myBoosts)
            {
                if (boost.Contains("Slot") && !boost["Slot"].IsBsonNull)
                {
                    myBoostSlots.Add(boost["Slot"].AsInt32);
                }
            }
        }

        var boostUrl = username != null ? $"https://t.me/boost/{username}" : $"https://t.me/boost/{channelId}";

        // Calculate premium audience (only for admins)
        MyTelegram.Schema.IStatsPercentValue? premiumAudience = null;
        var memberCol = _mongoDatabase.GetCollection<BsonDocument>("eventflow-channelmemberreadmodel");
        var totalMembers = await memberCol.CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("ChannelId", channelId));

        if (totalMembers > 0)
        {
            var userCol = _mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel");
            var memberDocs = await memberCol.Find(Builders<BsonDocument>.Filter.Eq("ChannelId", channelId))
                .Limit(10000)
                .ToListAsync();

            var userIds = memberDocs.Select(m => m["UserId"].AsInt64).ToList();
            var premiumCount = 0;

            if (userIds.Count > 0)
            {
                var userDocs = await userCol.Find(Builders<BsonDocument>.Filter.In("UserId", userIds))
                    .ToListAsync();

                premiumCount = userDocs.Count(u => u.Contains("Premium") && u["Premium"].AsBoolean);
            }

            premiumAudience = new MyTelegram.Schema.TStatsPercentValue
            {
                Part = premiumCount,
                Total = totalMembers
            };
        }

        // Load prepaid giveaways for this channel
        TVector<MyTelegram.Schema.IPrepaidGiveaway>? prepaidGiveaways = null;
        var giveawayCol = _mongoDatabase.GetCollection<BsonDocument>("giveaways");
        var giveawayFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("ChannelId", channelId),
            Builders<BsonDocument>.Filter.Eq("Status", "prepaid")
        );
        var giveawayDocs = await giveawayCol.Find(giveawayFilter).ToListAsync();

        if (giveawayDocs.Count > 0)
        {
            prepaidGiveaways = new TVector<MyTelegram.Schema.IPrepaidGiveaway>();
            foreach (var doc in giveawayDocs)
            {
                var type = doc["Type"].AsString;
                if (type == "stars")
                {
                    prepaidGiveaways.Add(new MyTelegram.Schema.TPrepaidStarsGiveaway
                    {
                        Id = doc["GiveawayId"].AsInt64,
                        Stars = doc["Stars"].AsInt64,
                        Quantity = doc["Winners"].AsInt32,
                        Boosts = doc.Contains("Boosts") ? doc["Boosts"].AsInt32 : 0,
                        Date = (int)doc["CreatedAt"].ToUniversalTime().Subtract(DateTime.UnixEpoch).TotalSeconds
                    });
                }
                else
                {
                    prepaidGiveaways.Add(new MyTelegram.Schema.TPrepaidGiveaway
                    {
                        Id = doc["GiveawayId"].AsInt64,
                        Months = doc.Contains("Months") ? doc["Months"].AsInt32 : 12,
                        Quantity = doc["Winners"].AsInt32,
                        Date = (int)doc["CreatedAt"].ToUniversalTime().Subtract(DateTime.UnixEpoch).TotalSeconds
                    });
                }
            }
        }

        return new TBoostsStatus
        {
            MyBoost = myBoost,
            Level = level,
            Boosts = totalBoosts,
            CurrentLevelBoosts = currentLevelBoosts,
            NextLevelBoosts = nextLevelBoosts,
            PremiumAudience = premiumAudience,
            BoostUrl = boostUrl,
            PrepaidGiveaways = prepaidGiveaways,
            MyBoostSlots = myBoostSlots
        };
    }

    private static int CalculateLevel(int boosts)
    {
        if (boosts < 1) return 0;
        if (boosts < 2) return 1;
        if (boosts < 5) return 2;
        if (boosts < 10) return 3;
        if (boosts < 25) return 4;
        if (boosts < 50) return 5;
        if (boosts < 100) return 6;
        if (boosts < 200) return 7;
        if (boosts < 500) return 8;
        if (boosts < 1000) return 9;
        return 10;
    }

    private static int GetBoostsForLevel(int level)
    {
        return level switch
        {
            0 => 0,
            1 => 1,
            2 => 2,
            3 => 5,
            4 => 10,
            5 => 25,
            6 => 50,
            7 => 100,
            8 => 200,
            9 => 500,
            10 => 1000,
            _ => 1000
        };
    }
}