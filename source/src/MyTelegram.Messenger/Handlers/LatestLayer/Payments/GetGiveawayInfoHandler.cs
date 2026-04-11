using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Obtain information about a <a href="https://corefork.telegram.org/api/giveaways">Telegram Premium giveaway »</a>.
/// Possible errors
/// Code Type Description
/// 400 MESSAGE_ID_INVALID The provided message id is invalid.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.getGiveawayInfo"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetGiveawayInfoHandler(
    IMongoDatabase database,
    IPeerHelper peerHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetGiveawayInfo, MyTelegram.Schema.Payments.IGiveawayInfo>
{
    protected override async Task<MyTelegram.Schema.Payments.IGiveawayInfo> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetGiveawayInfo obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null || peer.PeerType != PeerType.Channel)
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();

        var collection = database.GetCollection<BsonDocument>("giveaways");

        // First try: find by exact MsgId (original giveaway message)
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("ChannelId", peer.PeerId),
            Builders<BsonDocument>.Filter.Eq("MsgId", obj.MsgId)
        );
        var giveaway = await collection.Find(filter).FirstOrDefaultAsync();

        // Second try: find by MessageRandomId (for messages with MsgId=0)
        if (giveaway == null)
        {
            // Get message RandomId from messages collection
            var msgCol = database.GetCollection<BsonDocument>("eventflow-messagereadmodel");
            var msgFilter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("OwnerPeerId", peer.PeerId),
                Builders<BsonDocument>.Filter.Eq("MessageId", obj.MsgId)
            );
            var message = await msgCol.Find(msgFilter).FirstOrDefaultAsync();

            if (message != null && message.Contains("RandomId"))
            {
                var randomId = message["RandomId"].AsInt64;
                var randomIdFilter = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("ChannelId", peer.PeerId),
                    Builders<BsonDocument>.Filter.Eq("MessageRandomId", randomId)
                );
                giveaway = await collection.Find(randomIdFilter).FirstOrDefaultAsync();

                // Update MsgId if found
                if (giveaway != null)
                {
                    await collection.UpdateOneAsync(
                        Builders<BsonDocument>.Filter.Eq("_id", giveaway["_id"]),
                        Builders<BsonDocument>.Update.Set("MsgId", obj.MsgId)
                    );
                }
            }
        }

        // Third try: if not found, user might be clicking on results message
        // Results message has LaunchMsgId pointing to original giveaway
        // So we search for finished giveaways in this channel and check if MsgId is close to any LaunchMsgId
        if (giveaway == null)
        {
            var finishedFilter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("ChannelId", peer.PeerId),
                Builders<BsonDocument>.Filter.Eq("Status", "finished")
            );
            var finishedGiveaways = await collection.Find(finishedFilter).ToListAsync();

            // Find giveaway where obj.MsgId is likely the results message
            // Results message is sent after giveaway finishes, so it should be after LaunchMsgId
            // Try to find the closest giveaway before this message
            BsonDocument? closestGiveaway = null;
            int minDistance = int.MaxValue;

            foreach (var g in finishedGiveaways)
            {
                if (g.Contains("MsgId") && !g["MsgId"].IsBsonNull)
                {
                    var launchMsgId = g["MsgId"].AsInt32;
                    // Results message should be after launch message
                    if (obj.MsgId > launchMsgId)
                    {
                        var distance = obj.MsgId - launchMsgId;
                        if (distance < minDistance)
                        {
                            minDistance = distance;
                            closestGiveaway = g;
                        }
                    }
                }
            }

            // Accept if within reasonable range (5000 messages)
            if (closestGiveaway != null && minDistance < 5000)
            {
                giveaway = closestGiveaway;
            }
        }

        // Fourth try: fallback for giveaways with MsgId=0 (last resort)
        if (giveaway == null)
        {
            var fallbackFilter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("ChannelId", peer.PeerId),
                Builders<BsonDocument>.Filter.Or(
                    Builders<BsonDocument>.Filter.Eq("MsgId", 0),
                    Builders<BsonDocument>.Filter.Exists("MsgId", false)
                )
            );
            giveaway = await collection.Find(fallbackFilter).SortByDescending(x => x["StartDate"]).FirstOrDefaultAsync();

            // Update MsgId if found
            if (giveaway != null)
            {
                await collection.UpdateOneAsync(
                    Builders<BsonDocument>.Filter.Eq("_id", giveaway["_id"]),
                    Builders<BsonDocument>.Update.Set("MsgId", obj.MsgId)
                );
            }
        }

        if (giveaway == null)
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();

        var status = giveaway.Contains("Status") ? giveaway["Status"].AsString : "active";
        var startDate = giveaway["StartDate"].AsInt32;
        var userId = input.UserId;

        if (status == "finished")
        {
            // Check if user is a winner
            var winnerIds = giveaway.Contains("WinnerUserIds") && giveaway["WinnerUserIds"].IsBsonArray
                ? giveaway["WinnerUserIds"].AsBsonArray.Select(x => x.AsInt64).ToList()
                : new List<long>();
            var isWinner = winnerIds.Contains(userId);

            string? slug = null;
            long? starsPrize = null;

            if (isWinner)
            {
                var winnerGiveawayType = giveaway.Contains("Type") ? giveaway["Type"].AsString : "premium";

                if (winnerGiveawayType == "stars")
                {
                    // Stars giveaway - get stars amount directly
                    starsPrize = giveaway.Contains("Stars") && !giveaway["Stars"].IsBsonNull
                        ? giveaway["Stars"].AsInt64
                        : null;
                }
                else
                {
                    // Premium giveaway - find gift code for this user
                    // Use the actual giveaway MsgId, not the requested MsgId (which might be results message)
                    var giveawayMsgId = giveaway.Contains("MsgId") && !giveaway["MsgId"].IsBsonNull
                        ? giveaway["MsgId"].AsInt32
                        : 0;

                    var codeCol = database.GetCollection<BsonDocument>("premium_gift_codes");
                    var code = await codeCol.Find(
                        Builders<BsonDocument>.Filter.And(
                            Builders<BsonDocument>.Filter.Eq("ToId", userId),
                            Builders<BsonDocument>.Filter.Eq("GiveawayMsgId", giveawayMsgId)
                        )
                    ).FirstOrDefaultAsync();

                    if (code != null)
                    {
                        slug = code.Contains("Slug") ? code["Slug"].AsString : null;
                    }
                }
            }

            var winnersCount = winnerIds.Count;
            int? activatedCount = null;

            // Count activated codes only for Premium giveaways
            var type = giveaway.Contains("Type") ? giveaway["Type"].AsString : "premium";
            if (type == "premium" && winnersCount > 0)
            {
                var giveawayMsgId = giveaway.Contains("MsgId") && !giveaway["MsgId"].IsBsonNull
                    ? giveaway["MsgId"].AsInt32
                    : 0;

                var codeCol = database.GetCollection<BsonDocument>("premium_gift_codes");
                var count = (int)await codeCol.CountDocumentsAsync(
                    Builders<BsonDocument>.Filter.And(
                        Builders<BsonDocument>.Filter.Eq("GiveawayMsgId", giveawayMsgId),
                        Builders<BsonDocument>.Filter.Eq("Used", true)
                    )
                );
                if (count > 0)
                    activatedCount = count;
            }

            return new TGiveawayInfoResults
            {
                Winner = isWinner,
                Refunded = giveaway.Contains("Refunded") && giveaway["Refunded"].AsBoolean,
                StartDate = startDate,
                FinishDate = giveaway.Contains("UntilDate") ? giveaway["UntilDate"].AsInt32 : startDate + 86400,
                WinnersCount = winnersCount,
                GiftCodeSlug = slug,
                StarsPrize = starsPrize,
                ActivatedCount = activatedCount
            };
        }

        // Active giveaway - check if user is participating
        var memberCol = database.GetCollection<BsonDocument>("eventflow-channelmemberreadmodel");
        var isMember = await memberCol.Find(
            Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("ChannelId", peer.PeerId),
                Builders<BsonDocument>.Filter.Eq("UserId", userId)
            )
        ).AnyAsync();

        var onlyNewSubscribers = giveaway.Contains("OnlyNewSubscribers") && giveaway["OnlyNewSubscribers"].AsBoolean;
        int? joinedTooEarlyDate = null;
        long? adminDisallowedChatId = null;

        // Check if giveaway is preparing results (finished but winners not yet selected)
        var preparingResults = status == "active" && giveaway.Contains("UntilDate") &&
                               giveaway["UntilDate"].AsInt32 < (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (onlyNewSubscribers && isMember)
        {
            // Check if user joined before giveaway started
            var member = await memberCol.Find(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("ChannelId", peer.PeerId),
                    Builders<BsonDocument>.Filter.Eq("UserId", userId)
                )
            ).FirstOrDefaultAsync();

            if (member != null && member.Contains("JoinDate"))
            {
                var joinDate = member["JoinDate"].AsInt32;
                if (joinDate < startDate)
                    joinedTooEarlyDate = joinDate;
            }
        }

        // Check if user is subscribed to all additional channels
        if (isMember && joinedTooEarlyDate == null && giveaway.Contains("AdditionalChannels") && giveaway["AdditionalChannels"].IsBsonArray)
        {
            var additionalChannels = giveaway["AdditionalChannels"].AsBsonArray;
            foreach (var channelIdValue in additionalChannels)
            {
                var channelId = channelIdValue.AsInt64;
                var isAdditionalMember = await memberCol.Find(
                    Builders<BsonDocument>.Filter.And(
                        Builders<BsonDocument>.Filter.Eq("ChannelId", channelId),
                        Builders<BsonDocument>.Filter.Eq("UserId", userId)
                    )
                ).AnyAsync();

                if (!isAdditionalMember)
                {
                    // User not subscribed to required channel - can't participate
                    isMember = false;
                    break;
                }
            }
        }

        // Check if user is admin in any of the giveaway channels
        if (isMember && joinedTooEarlyDate == null)
        {
            // Check main channel
            var adminMember = await memberCol.Find(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("ChannelId", peer.PeerId),
                    Builders<BsonDocument>.Filter.Eq("UserId", userId),
                    Builders<BsonDocument>.Filter.Or(
                        Builders<BsonDocument>.Filter.Exists("AdminRights"),
                        Builders<BsonDocument>.Filter.Eq("IsCreator", true)
                    )
                )
            ).FirstOrDefaultAsync();

            if (adminMember != null)
            {
                adminDisallowedChatId = peer.PeerId;
            }
            else if (giveaway.Contains("AdditionalChannels") && giveaway["AdditionalChannels"].IsBsonArray)
            {
                // Check additional channels
                var additionalChannels = giveaway["AdditionalChannels"].AsBsonArray;
                foreach (var channelIdValue in additionalChannels)
                {
                    var channelId = channelIdValue.AsInt64;
                    var adminCheck = await memberCol.Find(
                        Builders<BsonDocument>.Filter.And(
                            Builders<BsonDocument>.Filter.Eq("ChannelId", channelId),
                            Builders<BsonDocument>.Filter.Eq("UserId", userId),
                            Builders<BsonDocument>.Filter.Or(
                                Builders<BsonDocument>.Filter.Exists("AdminRights"),
                                Builders<BsonDocument>.Filter.Eq("IsCreator", true)
                            )
                        )
                    ).FirstOrDefaultAsync();

                    if (adminCheck != null)
                    {
                        adminDisallowedChatId = channelId;
                        break;
                    }
                }
            }
        }

        return new TGiveawayInfo
        {
            Participating = isMember && joinedTooEarlyDate == null && adminDisallowedChatId == null,
            PreparingResults = preparingResults,
            StartDate = startDate,
            JoinedTooEarlyDate = joinedTooEarlyDate,
            AdminDisallowedChatId = adminDisallowedChatId,
            DisallowedCountry = null
        };
    }
}