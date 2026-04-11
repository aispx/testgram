using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;
/// <summary>
/// Obtain a list of bot commands for the specified bot scope and language code
/// Possible errors
/// Code Type Description
/// 400 USER_BOT_REQUIRED This method can only be called by a bot.
/// <para><c>See <a href="https://corefork.telegram.org/method/bots.getBotCommands"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✖] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class GetBotCommandsHandler(
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestGetBotCommands, TVector<MyTelegram.Schema.IBotCommand>>
{
    protected override async Task<TVector<MyTelegram.Schema.IBotCommand>> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Bots.RequestGetBotCommands obj)
    {
        var userReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(input.UserId));
        if (userReadModel == null || !userReadModel.Bot)
            RpcErrors.RpcErrors400.UserBotRequired.ThrowRpcError();

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

        var doc = await collection.Find(filter).FirstOrDefaultAsync();

        var commands = new TVector<IBotCommand>();
        if (doc != null && doc.Contains("Commands") && doc["Commands"].IsBsonArray)
        {
            foreach (var cmdBson in doc["Commands"].AsBsonArray)
            {
                if (cmdBson.IsBsonDocument)
                {
                    var cmdDoc = cmdBson.AsBsonDocument;
                    commands.Add(new TBotCommand
                    {
                        Command = cmdDoc["Command"].AsString,
                        Description = cmdDoc["Description"].AsString
                    });
                }
            }
        }

        return commands;
    }
}
