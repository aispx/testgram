using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;
/// <summary>
/// Clear bot commands for the specified bot scope and language code
/// Possible errors
/// Code Type Description
/// 400 LANG_CODE_INVALID The specified language code is invalid.
/// 400 USER_BOT_REQUIRED This method can only be called by a bot.
/// <para><c>See <a href="https://corefork.telegram.org/method/bots.resetBotCommands"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✖] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class ResetBotCommandsHandler(
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestResetBotCommands, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Bots.RequestResetBotCommands obj)
    {
        var userReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(input.UserId));
        if (userReadModel == null || !userReadModel.Bot)
            RpcErrors.RpcErrors400.UserBotRequired.ThrowRpcError();

        if (!string.IsNullOrEmpty(obj.LangCode) && obj.LangCode.Length > 10)
            RpcErrors.RpcErrors400.LangCodeInvalid.ThrowRpcError();

        var collection = mongoDatabase.GetCollection<BsonDocument>("bot_commands");
        var scopeType = obj.Scope.GetType().Name;
        long? peerId = null;

        if (obj.Scope is TBotCommandScopePeer scopePeer)
        {
            peerId = scopePeer.Peer switch
            {
                TInputPeerUser peerUser => peerUser.UserId,
                TInputPeerChat peerChat => peerChat.ChatId,
                TInputPeerChannel peerChannel => peerChannel.ChannelId,
                _ => null
            };
        }
        else if (obj.Scope is TBotCommandScopePeerUser scopePeerUser)
        {
            peerId = scopePeerUser.Peer switch
            {
                TInputPeerUser peerUser => peerUser.UserId,
                _ => null
            };
        }
        else if (obj.Scope is TBotCommandScopePeerAdmins scopePeerAdmins)
        {
            peerId = scopePeerAdmins.Peer switch
            {
                TInputPeerChat peerChat => peerChat.ChatId,
                TInputPeerChannel peerChannel => peerChannel.ChannelId,
                _ => null
            };
        }

        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("BotId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("ScopeType", scopeType),
            Builders<BsonDocument>.Filter.Eq("PeerId", peerId.HasValue ? (BsonValue)peerId.Value : BsonNull.Value),
            Builders<BsonDocument>.Filter.Eq("LangCode", obj.LangCode ?? "")
        );

        await collection.DeleteOneAsync(filter);

        return new TBoolTrue();
    }
}
