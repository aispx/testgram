using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services;
using MyTelegram.Messenger.Services.Interfaces;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;

internal sealed class SetCustomVerificationHandler(IMongoDatabase mongoDatabase, IUserAppService userAppService, IChannelAppService channelAppService, IMessageAppService messageAppService) : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestSetCustomVerification, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Bots.RequestSetCustomVerification obj)
    {
        var col = mongoDatabase.GetCollection<BotVerificationDocument>("bot-verifications");

        // Resolve bot user to get verifier settings
        long botId = input.UserId;
        if (obj.Bot != null && obj.Bot is TInputUser inputUser)
            botId = inputUser.UserId;

        var botReadModel = await userAppService.GetAsync(botId);
        if (botReadModel == null || !botReadModel.Bot)
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();

        // The badge is stored as issued by botId and rendered with that verifier's icon/company,
        // so the caller must be the bot itself or its owner. Otherwise any user could mint a
        // badge under an arbitrary organisation's identity.
        if (botId != input.UserId && !await IsBotOwnerAsync(botId, input.UserId))
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();

        // Resolve target peer
        long targetUserId = 0;
        long targetChannelId = 0;
        switch (obj.Peer)
        {
            case TInputPeerUser u: targetUserId = u.UserId; break;
            case TInputPeerSelf: targetUserId = input.UserId; break;
            case TInputPeerChannel c: targetChannelId = c.ChannelId; break;
            case TInputPeerChat ch: targetChannelId = ch.ChatId; break;
            default: RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError(); break;
        }

        var filter = targetUserId != 0
            ? Builders<BotVerificationDocument>.Filter.Eq(x => x.UserId, targetUserId)
            : Builders<BotVerificationDocument>.Filter.Eq(x => x.ChannelId, targetChannelId);

        if (!obj.Enabled)
        {
            await col.DeleteOneAsync(filter);
            return new TBoolTrue();
        }

        // Get icon/company from bot's verifier settings (stored in bot-verifier-settings collection)
        var settingsCol = mongoDatabase.GetCollection<BsonDocument>("bot-verifier-settings");
        var settingsDoc = await (await settingsCol.FindAsync(new BsonDocument("BotId", botId))).FirstOrDefaultAsync();
        long icon = settingsDoc != null ? settingsDoc["Icon"].ToInt64() : 0;
        string company = settingsDoc != null ? settingsDoc["Company"].AsString : "";
        string description = obj.CustomDescription ?? (settingsDoc != null && settingsDoc.Contains("CustomDescription") ? settingsDoc["CustomDescription"].AsString : company);

        var doc = new BotVerificationDocument
        {
            Id = targetUserId != 0 ? $"verification-user-{targetUserId}" : $"verification-channel-{targetChannelId}",
            BotId = botId,
            Icon = icon,
            Company = company,
            Description = description,
            UserId = targetUserId,
            ChannelId = targetChannelId,
        };

        await col.ReplaceOneAsync(filter, doc, new ReplaceOptions { IsUpsert = true });
        await NotifyVerificationGrantedAsync(botReadModel, targetUserId, targetChannelId, icon, description);
        return new TBoolTrue();
    }

    private async Task<bool> IsBotOwnerAsync(long botUserId, long ownerUserId)
    {
        var ownedViaBotOwners = await mongoDatabase.GetCollection<BsonDocument>("bot-owners")
            .Find(Builders<BsonDocument>.Filter.Eq("BotId", botUserId) &
                  Builders<BsonDocument>.Filter.Eq("OwnerId", ownerUserId))
            .Limit(1)
            .AnyAsync();
        if (ownedViaBotOwners)
            return true;

        return await mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel")
            .Find(Builders<BsonDocument>.Filter.Eq("UserId", botUserId) &
                  Builders<BsonDocument>.Filter.Eq("CreatorUserId", ownerUserId))
            .Limit(1)
            .AnyAsync();
    }

    private async Task NotifyVerificationGrantedAsync(
        IUserReadModel botReadModel,
        long targetUserId,
        long targetChannelId,
        long iconDocumentId,
        string description)
    {
        var recipients = await GetNotificationRecipientsAsync(targetUserId, targetChannelId);
        if (recipients.Count == 0)
        {
            return;
        }

        var botName = string.IsNullOrWhiteSpace(botReadModel.UserName)
            ? $"bot {botReadModel.UserId}"
            : $"@{botReadModel.UserName}";
        var company = string.IsNullOrWhiteSpace(description) ? botName : description;
        var iconText = await GetCustomEmojiAltAsync(iconDocumentId) ?? "⭐";
        var message = $"Бот {botName} выдал вам верификацию.\nПредложенный статус:\n{iconText} Аккаунт верифицирован организацией «{company}».";
        var entities = BuildCustomEmojiEntities(message, iconText, iconDocumentId);
        var now = DateTime.UtcNow.ToTimestamp();

        var sendInputs = recipients.Select(recipientId => new SendMessageInput(
            RequestInfo.Empty with
            {
                UserId = MyTelegramConsts.NotificationServiceUserId,
                Layer = MyTelegramConsts.Layer,
                Date = now,
                RequestId = Guid.NewGuid(),
                DeviceType = DeviceType.Android
            },
            MyTelegramConsts.NotificationServiceUserId,
            new Peer(PeerType.User, recipientId),
            message,
            Random.Shared.NextInt64(),
            entities: entities,
            sendMessageType: SendMessageType.Text,
            messageType: MessageType.Text
        )).ToList();

        await messageAppService.SendMessageAsync(sendInputs);
    }

    private async Task<List<long>> GetNotificationRecipientsAsync(long targetUserId, long targetChannelId)
    {
        var recipients = new HashSet<long>();
        if (targetUserId != 0)
        {
            var botOwnerDoc = await mongoDatabase.GetCollection<BsonDocument>("bot-owners")
                .Find(Builders<BsonDocument>.Filter.Eq("BotId", targetUserId))
                .FirstOrDefaultAsync();
            recipients.Add(botOwnerDoc != null ? botOwnerDoc["OwnerId"].ToInt64() : targetUserId);
        }
        else if (targetChannelId != 0)
        {
            var channelDoc = await mongoDatabase.GetCollection<BsonDocument>("eventflow-channelreadmodel")
                .Find(Builders<BsonDocument>.Filter.Eq("ChannelId", targetChannelId))
                .FirstOrDefaultAsync();
            if (channelDoc != null)
            {
                if (channelDoc.Contains("CreatorId") && !channelDoc["CreatorId"].IsBsonNull)
                {
                    recipients.Add(channelDoc["CreatorId"].ToInt64());
                }

                if (channelDoc.Contains("AdminList") && channelDoc["AdminList"].IsBsonArray)
                {
                    foreach (var admin in channelDoc["AdminList"].AsBsonArray.Where(x => x.IsBsonDocument))
                    {
                        var adminDoc = admin.AsBsonDocument;
                        if (adminDoc.Contains("UserId") && !adminDoc["UserId"].IsBsonNull)
                        {
                            recipients.Add(adminDoc["UserId"].ToInt64());
                        }
                    }
                }
            }
        }

        recipients.RemoveWhere(x => x <= 0 || x == MyTelegramConsts.NotificationServiceUserId);
        return recipients.ToList();
    }

    private async Task<string?> GetCustomEmojiAltAsync(long iconDocumentId)
    {
        if (iconDocumentId == 0)
        {
            return null;
        }

        var doc = await mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel")
            .Find(Builders<BsonDocument>.Filter.Eq("DocumentId", iconDocumentId))
            .FirstOrDefaultAsync();
        if (doc == null || !doc.Contains("Attributes2") || !doc["Attributes2"].IsBsonArray)
        {
            return null;
        }

        foreach (var attribute in doc["Attributes2"].AsBsonArray.Where(x => x.IsBsonDocument))
        {
            var attributeDoc = attribute.AsBsonDocument;
            if (attributeDoc.TryGetValue("_t", out var typeValue) &&
                typeValue.IsString &&
                typeValue.AsString == "TDocumentAttributeCustomEmoji" &&
                attributeDoc.TryGetValue("Alt", out var altValue) &&
                altValue.IsString &&
                !string.IsNullOrWhiteSpace(altValue.AsString))
            {
                return altValue.AsString;
            }
        }

        return null;
    }

    private static TVector<IMessageEntity>? BuildCustomEmojiEntities(string message, string iconText, long iconDocumentId)
    {
        if (iconDocumentId == 0)
        {
            return null;
        }

        var offset = message.IndexOf(iconText, StringComparison.Ordinal);
        if (offset < 0)
        {
            return null;
        }

        return new TVector<IMessageEntity>
        {
            new TMessageEntityCustomEmoji
            {
                Offset = offset,
                Length = iconText.Length,
                DocumentId = iconDocumentId
            }
        };
    }
}
