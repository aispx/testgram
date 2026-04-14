using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;

internal sealed class SetCustomVerificationHandler(IMongoDatabase mongoDatabase, IUserAppService userAppService, IChannelAppService channelAppService) : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestSetCustomVerification, IBool>
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
        return new TBoolTrue();
    }
}
