using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Launch a <a href="https://corefork.telegram.org/api/giveaways">prepaid giveaway »</a>.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.launchPrepaidGiveaway"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class LaunchPrepaidGiveawayHandler(
    IMongoDatabase mongoDatabase,
    IMessageAppService messageAppService,
    IPeerHelper peerHelper,
    IAccessHashHelper accessHashHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestLaunchPrepaidGiveaway, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Payments.RequestLaunchPrepaidGiveaway obj)
    {
        // Validate peer
        await accessHashHelper.CheckAccessHashAsync(input, obj.Peer);
        var targetPeer = peerHelper.GetPeer(obj.Peer, input.UserId);

        if (targetPeer.PeerType != PeerType.Channel)
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();

        // Get prepaid giveaway from MongoDB
        var giveawayCol = mongoDatabase.GetCollection<BsonDocument>("giveaways");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("GiveawayId", obj.GiveawayId),
            Builders<BsonDocument>.Filter.Eq("Status", "prepaid")
        );
        var giveaway = await giveawayCol.Find(filter).FirstOrDefaultAsync();

        if (giveaway == null)
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();

        // Extract giveaway data
        var type = giveaway["Type"].AsString; // "premium" or "stars"
        var winners = giveaway["Winners"].AsInt32;
        var untilDate = giveaway["UntilDate"].AsInt32;
        var onlyNewSubscribers = giveaway.Contains("OnlyNewSubscribers") && giveaway["OnlyNewSubscribers"].AsBoolean;
        var winnersAreVisible = giveaway.Contains("WinnersAreVisible") && giveaway["WinnersAreVisible"].AsBoolean;

        // Update purpose with actual data from request
        long? stars = null;
        int? months = null;
        string? prizeDescription = null;
        var additionalChannels = new List<long>();
        var countries = new List<string>();

        if (obj.Purpose is TInputStorePaymentPremiumGiveaway premiumPurpose)
        {
            months = giveaway.Contains("Months") ? giveaway["Months"].AsInt32 : 12;
            prizeDescription = premiumPurpose.PrizeDescription;

            if (premiumPurpose.AdditionalPeers != null)
            {
                foreach (var peer in premiumPurpose.AdditionalPeers)
                {
                    var p = peerHelper.GetPeer(peer, input.UserId);
                    if (p.PeerType == PeerType.Channel)
                        additionalChannels.Add(p.PeerId);
                }
            }

            if (premiumPurpose.CountriesIso2 != null)
                countries.AddRange(premiumPurpose.CountriesIso2);
        }
        else if (obj.Purpose is TInputStorePaymentStarsGiveaway starsPurpose)
        {
            stars = giveaway.Contains("Stars") ? giveaway["Stars"].AsInt64 : starsPurpose.Stars;
            prizeDescription = starsPurpose.PrizeDescription;

            if (starsPurpose.AdditionalPeers != null)
            {
                foreach (var peer in starsPurpose.AdditionalPeers)
                {
                    var p = peerHelper.GetPeer(peer, input.UserId);
                    if (p.PeerType == PeerType.Channel)
                        additionalChannels.Add(p.PeerId);
                }
            }

            if (starsPurpose.CountriesIso2 != null)
                countries.AddRange(starsPurpose.CountriesIso2);
        }

        // Create messageMediaGiveaway
        IMessageMedia media;
        if (type == "stars")
        {
            media = new TMessageMediaGiveaway
            {
                OnlyNewSubscribers = onlyNewSubscribers,
                WinnersAreVisible = winnersAreVisible,
                Channels = new TVector<long> { targetPeer.PeerId },
                CountriesIso2 = countries.Count > 0 ? new TVector<string>(countries) : null,
                PrizeDescription = prizeDescription,
                UntilDate = untilDate,
                Quantity = winners,
                Stars = stars ?? 0
            };
        }
        else
        {
            media = new TMessageMediaGiveaway
            {
                OnlyNewSubscribers = onlyNewSubscribers,
                WinnersAreVisible = winnersAreVisible,
                Channels = new TVector<long> { targetPeer.PeerId },
                CountriesIso2 = countries.Count > 0 ? new TVector<string>(countries) : null,
                PrizeDescription = prizeDescription,
                UntilDate = untilDate,
                Quantity = winners,
                Months = months ?? 12
            };
        }

        // Send giveaway message to channel
        var sendInput = new SendMessageInput(
            input.ToRequestInfo() with { ReqMsgId = 0 },
            input.UserId,
            targetPeer,
            string.Empty,
            Random.Shared.NextInt64(),
            sendMessageType: SendMessageType.Media,
            messageType: MessageType.Text,
            media: media
        );

        await messageAppService.SendMessageAsync([sendInput]);

        // Update giveaway status in MongoDB
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var update = Builders<BsonDocument>.Update
            .Set("Status", "active")
            .Set("ChannelId", targetPeer.PeerId)
            .Set("StartDate", now)
            .Set("CreatedBy", input.UserId)
            .Set("AdditionalChannels", new BsonArray(additionalChannels))
            .Set("Countries", new BsonArray(countries))
            .Set("PrizeDescription", prizeDescription ?? string.Empty)
            .Set("LaunchedAt", DateTime.UtcNow);

        await giveawayCol.UpdateOneAsync(filter, update);

        // Return null - message will arrive via event sourcing
        return null!;
    }
}