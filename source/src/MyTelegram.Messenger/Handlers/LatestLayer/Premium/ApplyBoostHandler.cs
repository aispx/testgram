using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Premium;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Premium;
/// <summary>
/// Apply one or more <a href="https://corefork.telegram.org/api/boost">boosts »</a> to a peer.
/// Possible errors
/// Code Type Description
/// 400 BOOSTS_EMPTY No boost slots were specified.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 SLOTS_EMPTY The specified slot list is empty.
/// <para><c>See <a href="https://corefork.telegram.org/method/premium.applyBoost"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ApplyBoostHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    ILogger<ApplyBoostHandler> logger) : RpcResultObjectHandler<MyTelegram.Schema.Premium.RequestApplyBoost, MyTelegram.Schema.Premium.IMyBoosts>
{
    protected override async Task<IMyBoosts> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Premium.RequestApplyBoost obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null || peer.PeerType != PeerType.Channel)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var channelId = peer.PeerId;
        var slots = obj.Slots ?? [0];

        var collection = mongoDatabase.GetCollection<BsonDocument>("channel_boosts");
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expires = now + (30 * 24 * 60 * 60);

        foreach (var slot in slots)
        {
            var existingBoost = await collection.Find(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
                    Builders<BsonDocument>.Filter.Eq("Slot", slot)
                )
            ).FirstOrDefaultAsync();

            if (existingBoost != null)
            {
                await collection.UpdateOneAsync(
                    Builders<BsonDocument>.Filter.Eq("_id", existingBoost["_id"]),
                    Builders<BsonDocument>.Update
                        .Set("ChannelId", channelId)
                        .Set("Date", now)
                        .Set("Expires", expires)
                );
            }
            else
            {
                await collection.InsertOneAsync(new BsonDocument
                {
                    ["_id"] = $"boost-{channelId}-{input.UserId}-{slot}",
                    ["ChannelId"] = channelId,
                    ["UserId"] = input.UserId,
                    ["Slot"] = slot,
                    ["Date"] = now,
                    ["Expires"] = expires,
                    ["Multiplier"] = 1,
                    ["Gift"] = false,
                    ["Giveaway"] = false,
                    ["Unclaimed"] = false
                });
            }
        }

        logger.LogInformation("User {UserId} applied {Count} boosts to channel {ChannelId}",
            input.UserId, slots.Count, channelId);

        var boosts = await collection.Find(Builders<BsonDocument>.Filter.Eq("UserId", input.UserId)).ToListAsync();

        var myBoosts = new List<IMyBoost>();
        var channelIds = new HashSet<long>();

        foreach (var boost in boosts)
        {
            var boostChannelId = boost["ChannelId"].AsInt64;
            channelIds.Add(boostChannelId);

            myBoosts.Add(new TMyBoost
            {
                Slot = boost["Slot"].AsInt32,
                Peer = new TPeerChannel { ChannelId = boostChannelId },
                Date = boost["Date"].AsInt32,
                Expires = boost["Expires"].AsInt32
            });
        }

        var channelCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-channelreadmodel");
        var channelFilter = Builders<BsonDocument>.Filter.In("ChannelId", channelIds);
        var channels = await channelCol.Find(channelFilter).ToListAsync();

        var chats = new List<IChat>();
        foreach (var channel in channels)
        {
            var broadcast = channel.Contains("Broadcast") && channel["Broadcast"].AsBoolean;
            var megagroup = channel.Contains("MegaGroup") && channel["MegaGroup"].AsBoolean;

            // Fix for old channels without Megagroup flag: if not broadcast, it's a megagroup
            if (!broadcast && !megagroup)
            {
                megagroup = true;
            }

            chats.Add(new TChannel
            {
                Id = channel["ChannelId"].AsInt64,
                AccessHash = channel["AccessHash"].AsInt64,
                Title = channel["Title"].AsString,
                Username = channel.Contains("UserName") ? channel["UserName"].AsString : null,
                Photo = new TChatPhotoEmpty(),
                Date = channel.Contains("Date") ? channel["Date"].AsInt32 : 0,
                RestrictionReason = [],
                Broadcast = broadcast,
                Megagroup = megagroup
            });
        }

        return new TMyBoosts
        {
            MyBoosts = new TVector<IMyBoost>(myBoosts),
            Chats = new TVector<IChat>(chats),
            Users = []
        };
    }
}