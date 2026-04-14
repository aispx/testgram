using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
/// <summary>
/// Get full info about a <a href="https://corefork.telegram.org/api/channel#supergroups">supergroup</a>, <a href="https://corefork.telegram.org/api/channel#gigagroups">gigagroup</a> or <a href="https://corefork.telegram.org/api/channel#channels">channel</a>
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 406 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 403 CHANNEL_PUBLIC_GROUP_NA channel/supergroup not available.
/// 400 CHAT_NOT_MODIFIED No changes were made to chat information because the new information you passed is identical to the current information.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.getFullChannel"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class GetFullChannelHandler(IQueryProcessor queryProcessor, //ILayeredService<IChatConverter> layeredService,
 IUserConverterService userConverterService, IChatConverterService chatConverterService, IAccessHashHelper accessHashHelper, IPhotoAppService photoAppService, ILogger<GetFullChannelHandler> logger, IChannelAppService channelAppService, IChannelAdminRightsChecker channelAdminRightsChecker, IMongoDatabase mongoDatabase) : RpcResultObjectHandler<RequestGetFullChannel, MyTelegram.Schema.Messages.IChatFull>
{
    protected override async Task<MyTelegram.Schema.Messages.IChatFull> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Channels.RequestGetFullChannel obj)
    {
        if (obj.Channel is TInputChannel inputChannel)
        {
            var channelId = inputChannel.ChannelId;
            await accessHashHelper.CheckAccessHashAsync(input, channelId, inputChannel.AccessHash, AccessHashType.Channel);
            var channelReadModel = await channelAppService.GetAsync(channelId);
            if (channelReadModel == null !)
            {
                RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
            }

            var channelFullReadModel = await channelAppService.GetChannelFullAsync(channelId);
            if (channelFullReadModel == null)
            {
                RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
            }

            if (await channelAppService.SendRpcErrorIfNotChannelMemberAsync(input, channelReadModel!))
            {
                return null !;
            }

            var dialogReadModel = await queryProcessor.ProcessAsync(new GetDialogByIdQuery(DialogId.Create(input.UserId, PeerType.Channel, channelId).Value));
            if (dialogReadModel == null)
            {
                logger.LogWarning("Dialog not exists, userId: {UserId}, toPeer: {ToPeer}", input.UserId, new Peer(PeerType.Channel, channelId));
            }
            else
            {
                channelFullReadModel!.ReadInboxMaxId = dialogReadModel.ReadInboxMaxId;
                channelFullReadModel.ReadOutboxMaxId = dialogReadModel.ReadOutboxMaxId;
                var maxId = new[]
                {
                    dialogReadModel.ReadInboxMaxId,
                    dialogReadModel.ReadOutboxMaxId,
                    dialogReadModel.ChannelHistoryMinId
                }.Max();
                channelFullReadModel.UnreadCount = channelReadModel!.TopMessageId - maxId;
            }

            var channelMemberReadModel = await queryProcessor.ProcessAsync(new GetChannelMemberByUserIdQuery(channelId, input.UserId));
            var peerNotifySettings = await queryProcessor.ProcessAsync(new GetPeerNotifySettingsByIdQuery(PeerNotifySettingsId.Create(input.UserId, PeerType.Channel, channelId).Value));
            var photoReadModel = await photoAppService.GetAsync(channelReadModel!.PhotoId);
            IChatInviteReadModel? chatInviteReadModel = null;
            if (channelReadModel.AdminList.Any(p => p.UserId == input.UserId))
            {
                chatInviteReadModel = await queryProcessor.ProcessAsync(new GetPermanentChatInviteQuery(channelId, input.UserId));
            }

            var chatFull = chatConverterService.ToChannelFull(input, channelReadModel, photoReadModel, channelFullReadModel!, channelMemberReadModel, peerNotifySettings, chatInviteReadModel, input.Layer);
            var fullChat = chatFull.FullChat;
            if (fullChat is ILayeredChannelFull layeredChannelFull)
            {
                layeredChannelFull.ViewForumAsMessages = dialogReadModel?.ViewForumAsMessages ?? false;
                layeredChannelFull.ParticipantsHidden = channelReadModel.ParticipantsHidden;
                if (channelMemberReadModel == null || channelMemberReadModel.Left || channelMemberReadModel.Kicked)
                {
                    layeredChannelFull.CanViewParticipants = false;
                }

                // Set pending requests for channel admin
                await SetRecentRequestersAsync(input, layeredChannelFull, chatFull);
                await SetStarGiftsInfoAsync(channelId, layeredChannelFull);
                await SetBoostsInfoAsync(input.UserId, channelId, layeredChannelFull);
                await SetBotVerificationAsync(channelId, layeredChannelFull, chatFull);
                await SetPinnedMsgIdAsync(channelId, layeredChannelFull);
            }

            IChat? linkedChannel = null;
            if (channelFullReadModel!.LinkedChatId.HasValue)
            {
                var linkedChannelReadModel = await channelAppService.GetAsync(channelFullReadModel.LinkedChatId.Value);
                if (linkedChannelReadModel != null !)
                {
                    var linkedChannelPhotoReadModel = await photoAppService.GetAsync(linkedChannelReadModel.PhotoId);
                    var linkedChannelMemberReadModel = await queryProcessor.ProcessAsync(new GetChannelMemberByUserIdQuery(linkedChannelReadModel.ChannelId, input.UserId));
                    linkedChannel = chatConverterService.ToChannel(input, linkedChannelReadModel, linkedChannelPhotoReadModel, linkedChannelMemberReadModel, linkedChannelMemberReadModel == null || linkedChannelMemberReadModel.Left, input.Layer);
                //r.Chats.Add(linkedChannel);
                }
            }

            if (linkedChannel != null)
            {
                chatFull.Chats.Add(linkedChannel);
            }

            // Add monoforum to chats so client can cache it
            if (channelReadModel!.LinkedMonoforumId.HasValue)
            {
                var monoforumReadModel = await channelAppService.GetAsync(channelReadModel.LinkedMonoforumId.Value);
                if (monoforumReadModel != null)
                {
                    var monoforumPhotoReadModel = await photoAppService.GetAsync(monoforumReadModel.PhotoId);
                    var monoforumMemberReadModel = await queryProcessor.ProcessAsync(new GetChannelMemberByUserIdQuery(monoforumReadModel.ChannelId, input.UserId));
                    var monoforumChat = chatConverterService.ToChannel(input, monoforumReadModel, monoforumPhotoReadModel, monoforumMemberReadModel, monoforumMemberReadModel == null || monoforumMemberReadModel.Left, input.Layer);
                    chatFull.Chats.Add(monoforumChat);
                }
            }

            return chatFull;
        }

        throw new NotImplementedException();
    }

    private async Task SetStarGiftsInfoAsync(long channelId, ILayeredChannelFull channelFull)
    {
        if (channelFull is TChannelFull tFull) tFull.StargiftsAvailable = true;
        var col = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
        var count = (int)await col.CountDocumentsAsync(
            Builders<SavedStarGiftDocument>.Filter.And(
                Builders<SavedStarGiftDocument>.Filter.Eq(d => d.OwnerChannelId, channelId),
                Builders<SavedStarGiftDocument>.Filter.Eq(d => d.Saved, true)
            ));
        if (count > 0) channelFull.StargiftsCount = count;
    }

    private async Task SetRecentRequestersAsync(IRequestInput input, ILayeredChannelFull layeredChannelFull, MyTelegram.Schema.Messages.IChatFull chatFull)
    {
        var channelId = layeredChannelFull.Id;
        if (await channelAdminRightsChecker.HasChatAdminRightAsync(channelId, input.UserId, p => p.InviteUsers))
        {
            var pendingRequestsCount = await queryProcessor.ProcessAsync(new GetPendingRequestsCountQuery(channelId));
            if (pendingRequestsCount > 0)
            {
                layeredChannelFull.RequestsPending = pendingRequestsCount;
                var recentRequesters = await queryProcessor.ProcessAsync(new GetRecentRequestUserIdListQuery(channelId, 5));
                layeredChannelFull.RecentRequesters = [..recentRequesters];
                var users = await userConverterService.GetUserListAsync(input, [..recentRequesters], false, false, input.Layer);
                chatFull.Users = [..users];
            }
        }
    }

    private async Task SetBotVerificationAsync(long channelId, ILayeredChannelFull channelFull, MyTelegram.Schema.Messages.IChatFull chatFull)
    {
        var col = mongoDatabase.GetCollection<MyTelegram.Messenger.Services.BotVerificationDocument>("bot-verifications");
        var doc = await (await col.FindAsync(Builders<MyTelegram.Messenger.Services.BotVerificationDocument>.Filter.Eq(x => x.ChannelId, channelId))).FirstOrDefaultAsync();
        if (doc == null) return;

        var bv = new TBotVerification { BotId = doc.BotId, Icon = doc.Icon, Description = doc.Description };
        if (channelFull is TChannelFull tFull) tFull.BotVerification = bv;

        var ch = chatFull.Chats?.OfType<TChannel>().FirstOrDefault(c => c.Id == channelId);
        if (ch != null) ch.BotVerificationIcon = doc.Icon;
    }

    private async Task SetBoostsInfoAsync(long userId, long channelId, ILayeredChannelFull channelFull)
    {
        if (channelFull is not TChannelFull tFull) return;

        var col = mongoDatabase.GetCollection<MongoDB.Bson.BsonDocument>("channel_boosts");

        // Count total boosts for this channel
        var totalBoosts = await col.CountDocumentsAsync(
            Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("ChannelId", channelId));

        // Count user's boosts for this channel
        var userBoosts = await col.CountDocumentsAsync(
            Builders<MongoDB.Bson.BsonDocument>.Filter.And(
                Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("ChannelId", channelId),
                Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("UserId", userId)
            ));

        if (userBoosts > 0)
        {
            tFull.BoostsApplied = (int)userBoosts;
        }

        // Read boost-level features from channel read model
        var channelCol = mongoDatabase.GetCollection<MongoDB.Bson.BsonDocument>("eventflow-channelreadmodel");
        var channelDoc = await channelCol.Find(
            Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("ChannelId", channelId)
        ).FirstOrDefaultAsync();

        if (channelDoc != null)
        {
            // RestrictedSponsored (flags2.11)
            if (channelDoc.Contains("RestrictedSponsored") && channelDoc["RestrictedSponsored"].AsBoolean)
            {
                tFull.RestrictedSponsored = true;
            }

            // BoostsUnrestrict (flags2.9)
            if (channelDoc.Contains("BoostsToUnblockRestrictions") && !channelDoc["BoostsToUnblockRestrictions"].IsBsonNull)
            {
                tFull.BoostsUnrestrict = channelDoc["BoostsToUnblockRestrictions"].AsInt32;
            }

            // EmojiSet (flags.15) - load stickerset
            if (channelDoc.Contains("EmojiSet") && !channelDoc["EmojiSet"].IsBsonNull)
            {
                var emojiSetId = channelDoc["EmojiSet"].AsInt64;
                var stickerSetCol = mongoDatabase.GetCollection<MongoDB.Bson.BsonDocument>("eventflow-stickersetreadmodel");
                var stickerSetDoc = await stickerSetCol.Find(
                    Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("StickerSetId", emojiSetId)
                ).FirstOrDefaultAsync();

                if (stickerSetDoc != null)
                {
                    tFull.Emojiset = new MyTelegram.Schema.TStickerSet
                    {
                        Id = stickerSetDoc["StickerSetId"].AsInt64,
                        AccessHash = stickerSetDoc["AccessHash"].AsInt64,
                        Title = stickerSetDoc["Title"].AsString,
                        ShortName = stickerSetDoc.Contains("ShortName") ? stickerSetDoc["ShortName"].AsString : string.Empty,
                        Count = stickerSetDoc.Contains("Count") ? stickerSetDoc["Count"].AsInt32 : 0,
                        Hash = 0
                    };
                }
            }

            // SendPaidMessagesStars (paid messages price)
            if (channelDoc.Contains("SendPaidMessagesStars") && !channelDoc["SendPaidMessagesStars"].IsBsonNull)
            {
                tFull.SendPaidMessagesStars = channelDoc["SendPaidMessagesStars"].AsInt64;
            }
        }
    }

    private async Task SetPinnedMsgIdAsync(long channelId, ILayeredChannelFull channelFull)
    {
        try
        {
            // Query latest pinned message in channel
            var collection = mongoDatabase.GetCollection<MongoDB.Bson.BsonDocument>("eventflow-messagereadmodel");
            var filter = Builders<MongoDB.Bson.BsonDocument>.Filter.And(
                Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("OwnerPeerId", channelId),
                Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("Pinned", true)
            );

            var pinnedMessage = await collection.Find(filter)
                .SortByDescending(m => m["MessageId"])
                .Limit(1)
                .FirstOrDefaultAsync();

            if (pinnedMessage != null && channelFull is TChannelFull tChannelFull)
            {
                tChannelFull.PinnedMsgId = pinnedMessage["MessageId"].AsInt32;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load pinned message for channel {ChannelId}", channelId);
        }
    }
}