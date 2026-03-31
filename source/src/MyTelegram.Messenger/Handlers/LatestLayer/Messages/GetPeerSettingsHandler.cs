using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.Extensions.Logging;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get peer settings
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHANNEL_MONOFORUM_UNSUPPORTED <a href="https://corefork.telegram.org/api/channel#monoforums">Monoforums</a> do not support this feature.
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getPeerSettings"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetPeerSettingsHandler(
    IPeerSettingsAppService peerSettingsAppService,
    IPeerHelper peerHelper,
    IObjectMapper objectMapper,
    IQueryProcessor queryProcessor,
    IAccessHashHelper accessHashHelper,
    IContactAppService contactAppService,
    IChannelAppService channelAppService,
    ILayeredService<IPeerSettingsConverter> layeredService,
    IMongoDatabase database,
    ILogger<GetPeerSettingsHandler> logger) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetPeerSettings, MyTelegram.Schema.Messages.IPeerSettings>
{
    protected override async Task<MyTelegram.Schema.Messages.IPeerSettings> HandleCoreAsync(IRequestInput input, RequestGetPeerSettings obj)
    {
        await accessHashHelper.CheckAccessHashAsync(input, obj.Peer);
        var userId = input.UserId;
        var peer = peerHelper.GetPeer(obj.Peer, userId);
        if (peer.PeerType == PeerType.Channel)
        {
            if (await channelAppService.SendRpcErrorIfNotChannelMemberAsync(input, peer.PeerId))
            {
                return null !;
            }
        }

        if (peer.PeerId == MyTelegramConsts.NotificationServiceUserId || peer.PeerType == PeerType.Self)
        {
            return new MyTelegram.Schema.Messages.TPeerSettings
            {
                Chats = new(),
                Users = new(),
                Settings = new Schema.TPeerSettings()
            };
        }

        //IContactReadModel? contactReadModel = null;
        ContactType? contactType = null;
        if (peer.PeerType == PeerType.User)
        {
            //contactReadModel = await _queryProcessor.ProcessAsync(new GetContactQuery(userId, peer.PeerId));
            var contactReadModels = await queryProcessor.ProcessAsync(new GetContactListBySelfIdAndTargetUserIdQuery(input.UserId, peer.PeerId));
            contactType = contactAppService.GetContactType(input.UserId, peer.PeerId, contactReadModels);
        }

        var r = await peerSettingsAppService.GetPeerSettingsAsync(userId, peer.PeerId);
        var settings = layeredService.GetConverter(input.Layer).ToPeerSettings(input.UserId, peer.PeerId, r, contactType);
        if (r == null && peer.PeerType == PeerType.Channel)
        {
            settings = new MyTelegram.Schema.TPeerSettings();
        }

        if (peer.PeerType == PeerType.User)
        {
            var targetGps = await queryProcessor.ProcessAsync(new GetGlobalPrivacySettingsQuery(peer.PeerId));
            if (targetGps?.NoncontactPeersPaidStars > 0)
                settings.ChargePaidMessageStars = targetGps.NoncontactPeersPaidStars;
        }

        // Check for connected business bot
        await SetBusinessBotFieldsAsync(settings, userId, peer.PeerId);

        // DEBUG: Force set for user 2010001
        if (userId == 2010001 && settings is Schema.TPeerSettings debugSettings)
        {
            debugSettings.BusinessBotId = 2667006;
            debugSettings.BusinessBotManageUrl = "tg://resolve?domain=botfather&start=manage_2667006";
            debugSettings.BusinessBotCanReply = true;
        }

        var peerSettings = new MyTelegram.Schema.Messages.TPeerSettings
        {
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Settings = settings
        };
        return peerSettings;
    }

    private async Task SetBusinessBotFieldsAsync(Schema.IPeerSettings settings, long selfUserId, long targetUserId)
    {
        try
        {
            // Find connected bot for the SELF user (who is opening the chat - bot owner)
            var collection = database.GetCollection<BsonDocument>("connected_business_bots");
            var filter = Builders<BsonDocument>.Filter.Eq("UserId", selfUserId);
            var connection = await collection.Find(filter).FirstOrDefaultAsync();

            if (connection != null && settings is Schema.TPeerSettings tSettings)
            {
                var botId = connection["BotId"].AsInt64;
                var connectionId = connection.Contains("ConnectionId") ? connection["ConnectionId"].AsString : "";

                // Check recipients - should this chat be managed?
                var recipientsDoc = connection["Recipients"].AsBsonDocument;
                bool excludeSelected = recipientsDoc.Contains("ExcludeSelected") && recipientsDoc["ExcludeSelected"].AsBoolean;

                // Default: if bot is connected, manage all chats
                bool shouldManage = true;

                // Check explicit exclusion
                if (recipientsDoc.Contains("ExcludeUsers") && !recipientsDoc["ExcludeUsers"].IsBsonNull)
                {
                    var excludeArray = recipientsDoc["ExcludeUsers"].AsBsonArray;
                    if (excludeArray.Any(u => u.AsInt64 == targetUserId))
                    {
                        shouldManage = false;
                    }
                }

                // If explicit users list exists and not empty - only they see banner
                if (shouldManage &&
                    recipientsDoc.Contains("Users") &&
                    !recipientsDoc["Users"].IsBsonNull)
                {
                    var usersArray = recipientsDoc["Users"].AsBsonArray;
                    if (usersArray.Count > 0 && !usersArray.Any(u => u.AsInt64 == targetUserId))
                    {
                        shouldManage = false;
                    }
                }

                if (shouldManage)
                {
                    // Get bot user for username
                    var botUser = await queryProcessor.ProcessAsync(new GetUserByIdQuery(botId));
                    var manageUrl = botUser?.UserName != null
                        ? $"https://t.me/{botUser.UserName}?start=bizbot_{connectionId}"
                        : $"https://t.me/{botId}";

                    // CRITICAL: Both business_bot_id and business_bot_manage_url must be set (flag 13)
                    tSettings.BusinessBotId = botId;
                    tSettings.BusinessBotManageUrl = manageUrl;

                    // Check if bot is paused in this specific chat
                    var pausedCollection = database.GetCollection<BsonDocument>("paused_business_bot_chats");
                    var pausedFilter = Builders<BsonDocument>.Filter.And(
                        Builders<BsonDocument>.Filter.Eq("UserId", selfUserId),
                        Builders<BsonDocument>.Filter.Eq("PeerId", targetUserId)
                    );
                    var pausedDoc = await pausedCollection.Find(pausedFilter).FirstOrDefaultAsync();
                    tSettings.BusinessBotPaused = pausedDoc != null && pausedDoc.Contains("Paused") && pausedDoc["Paused"].AsBoolean;

                    // Check bot rights - can it reply?
                    var rightsDoc = connection.Contains("Rights") ? connection["Rights"].AsBsonDocument : null;
                    tSettings.BusinessBotCanReply = rightsDoc != null && rightsDoc.Contains("Reply") && rightsDoc["Reply"].AsBoolean;
                }
            }
        }
        catch
        {
            // Ignore errors
        }
    }
}