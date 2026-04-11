using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;
/// <summary>
/// Set bot command list
/// Possible errors
/// Code Type Description
/// 400 BOT_COMMAND_DESCRIPTION_INVALID The specified command description is invalid.
/// 400 BOT_COMMAND_INVALID The specified command is invalid.
/// 400 LANG_CODE_INVALID The specified language code is invalid.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 USER_BOT_REQUIRED This method can only be called by a bot.
/// 400 USER_ID_INVALID The provided user ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/bots.setBotCommands"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✖] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class SetBotCommandsHandler(
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestSetBotCommands, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Bots.RequestSetBotCommands obj)
    {
        var userReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(input.UserId));
        if (userReadModel == null || !userReadModel.Bot)
            RpcErrors.RpcErrors400.UserBotRequired.ThrowRpcError();

        if (!string.IsNullOrEmpty(obj.LangCode) && obj.LangCode.Length > 10)
            RpcErrors.RpcErrors400.LangCodeInvalid.ThrowRpcError();

        foreach (var command in obj.Commands)
        {
            if (command is TBotCommand botCommand)
            {
                if (string.IsNullOrWhiteSpace(botCommand.Command) || botCommand.Command.Length > 32)
                    RpcErrors.RpcErrors400.BotCommandInvalid.ThrowRpcError();

                if (string.IsNullOrWhiteSpace(botCommand.Description) || botCommand.Description.Length > 256)
                    RpcErrors.RpcErrors400.BotCommandDescriptionInvalid.ThrowRpcError();
            }
        }

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

        var commandsArray = new BsonArray();
        foreach (var command in obj.Commands)
        {
            if (command is TBotCommand botCommand)
            {
                commandsArray.Add(new BsonDocument
                {
                    ["Command"] = botCommand.Command,
                    ["Description"] = botCommand.Description
                });
            }
        }

        var update = Builders<BsonDocument>.Update
            .Set("BotId", input.UserId)
            .Set("ScopeType", scopeType)
            .Set("PeerId", peerId.HasValue ? (BsonValue)peerId.Value : BsonNull.Value)
            .Set("LangCode", obj.LangCode ?? "")
            .Set("Commands", commandsArray)
            .Set("UpdatedAt", DateTime.UtcNow);

        await collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });

        return new TBoolTrue();
    }
}
