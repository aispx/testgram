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
    IMessageAppService messageAppService,
    IChatConverterService chatConverterService,
    IQueryProcessor queryProcessor,
    ILogger<ApplyBoostHandler> logger) : RpcResultObjectHandler<MyTelegram.Schema.Premium.RequestApplyBoost, MyTelegram.Schema.Premium.IMyBoosts>
{
    private static long GetInt64(BsonValue v)
    {
        return v.BsonType switch
        {
            BsonType.Int64 => v.AsInt64,
            BsonType.Int32 => v.AsInt32,
            BsonType.Double => (long)v.AsDouble,
            _ => throw new InvalidCastException($"Cannot convert {v.BsonType} to Int64")
        };
    }

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

        // Send messageActionBoostApply service message
        var action = new TMessageActionBoostApply { Boosts = slots.Count };
        var toPeer = new Peer(PeerType.Channel, channelId);

        var sendInput = new SendMessageInput(
            input.ToRequestInfo() with { ReqMsgId = 0 },
            input.UserId,
            toPeer,
            string.Empty,
            Random.Shared.NextInt64(),
            sendMessageType: SendMessageType.MessageService,
            messageType: MessageType.Text,
            messageAction: action
        );
        await messageAppService.SendMessageAsync([sendInput]);

        var boosts = await collection.Find(Builders<BsonDocument>.Filter.Eq("UserId", input.UserId)).ToListAsync();

        var myBoosts = new List<IMyBoost>();
        var channelIds = new HashSet<long>();

        foreach (var boost in boosts)
        {
            var boostChannelId = GetInt64(boost["ChannelId"]);

            // Add to channelIds only if boost is active (ChannelId != 0)
            if (boostChannelId != 0)
            {
                channelIds.Add(boostChannelId);
            }

            myBoosts.Add(new TMyBoost
            {
                Slot = boost["Slot"].AsInt32,
                // Peer is null for free slots (ChannelId == 0) - this is correct!
                Peer = boostChannelId != 0 ? new TPeerChannel { ChannelId = boostChannelId } : null,
                Date = boost["Date"].AsInt32,
                Expires = boost["Expires"].AsInt32,
                CooldownUntilDate = boost.Contains("CooldownUntilDate") ? boost["CooldownUntilDate"].AsInt32 : null
            });
        }

        var channelIdList = channelIds.ToList();
        var channelMemberReadModels = channelIdList.Count == 0
            ? []
            : await queryProcessor.ProcessAsync(new GetChannelMemberListByChannelIdListQuery(input.UserId, channelIdList));
        var chats = await chatConverterService.GetChannelListAsync(input, channelIdList, channelMemberReadModels, input.Layer);

        return new TMyBoosts
        {
            MyBoosts = new TVector<IMyBoost>(myBoosts),
            Chats = new TVector<IChat>(chats),
            Users = new TVector<IUser>()
        };
    }
}
