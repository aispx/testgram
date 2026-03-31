using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Account;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Connect a <a href="https://corefork.telegram.org/api/bots/connected-business-bots">business bot »</a> to the current account, or to change the current connection settings.
/// Possible errors
/// Code Type Description
/// 400 BOT_BUSINESS_MISSING The specified bot is not a business bot (the <a href="https://corefork.telegram.org/constructor/user">user</a>.<code>bot_business</code> flag is not set).
/// 400 BUSINESS_RECIPIENTS_EMPTY You didn't set any flag in inputBusinessBotRecipients, thus the bot cannot work with <em>any</em> peer.
/// 403 PREMIUM_ACCOUNT_REQUIRED A premium account is required to execute this action.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.updateConnectedBot"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class UpdateConnectedBotHandler : RpcResultObjectHandler<RequestUpdateConnectedBot, IUpdates>
{
    private readonly IMongoDatabase _database;
    private readonly IQueryProcessor _queryProcessor;
    private readonly IObjectMessageSender _objectMessageSender;
    private readonly IUserConverterService _userConverterService;

    public UpdateConnectedBotHandler(IMongoDatabase database, IQueryProcessor queryProcessor, IObjectMessageSender objectMessageSender, IUserConverterService userConverterService)
    {
        _database = database;
        _queryProcessor = queryProcessor;
        _objectMessageSender = objectMessageSender;
        _userConverterService = userConverterService;
    }

    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestUpdateConnectedBot obj)
    {
        var userId = input.UserId;

        // Get bot user ID
        long botId = obj.Bot switch
        {
            TInputUser inputUser => inputUser.UserId,
            TInputUserSelf => userId,
            _ => 0
        };

        if (botId == 0)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        // Check if bot exists and is a business bot
        var botUser = await _queryProcessor.ProcessAsync(new GetUserByIdQuery(botId));
        if (botUser == null || !botUser.Bot)
        {
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
        }

        // Check bot_business flag in MongoDB directly
        var userCollection = _database.GetCollection<BsonDocument>("eventflow-userreadmodel");
        var botUserDoc = await userCollection.Find(Builders<BsonDocument>.Filter.Eq("UserId", botId)).FirstOrDefaultAsync();
        if (botUserDoc == null || !botUserDoc.Contains("BotBusiness") || !botUserDoc["BotBusiness"].AsBoolean)
        {
            RpcErrors.RpcErrors400.BotBusinessMissing.ThrowRpcError();
        }

        var collection = _database.GetCollection<BsonDocument>("connected_business_bots");

        if (obj.Deleted)
        {
            // Disconnect bot
            var filter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("UserId", userId),
                Builders<BsonDocument>.Filter.Eq("BotId", botId)
            );
            var existingConnection = await collection.Find(filter).FirstOrDefaultAsync();
            if (existingConnection != null)
            {
                var existingConnectionId = existingConnection["ConnectionId"].AsString;
                await collection.DeleteOneAsync(filter);

                // Send updateBotBusinessConnect to bot with disabled=true
                var deleteUpdate = new TUpdateBotBusinessConnect
                {
                    Connection = new TBotBusinessConnection
                    {
                        ConnectionId = existingConnectionId,
                        UserId = userId,
                        DcId = 1,
                        Date = CurrentDate,
                        Disabled = true
                    },
                    Qts = await GetNextQtsAsync(botId)
                };

                var deleteUpdates = new TUpdates
                {
                    Updates = new TVector<IUpdate> { deleteUpdate },
                    Users = new TVector<IUser>(),
                    Chats = new TVector<IChat>(),
                    Date = CurrentDate
                };

                await _objectMessageSender.PushMessageToPeerAsync(
                    new Peer(PeerType.User, botId),
                    deleteUpdates,
                    pts: 0
                );
            }

            return new TUpdates
            {
                Updates = new TVector<IUpdate>(),
                Users = new TVector<IUser>(),
                Chats = new TVector<IChat>(),
                Date = CurrentDate
            };
        }

        // Validate recipients
        if (obj.Recipients is TInputBusinessBotRecipients recipients)
        {
            // If ExcludeSelected=true with empty lists, it means "all chats" - this is valid
            bool hasExcludeMode = recipients.ExcludeSelected &&
                                  (recipients.ExcludeUsers == null || recipients.ExcludeUsers.Count == 0);

            bool hasIncludeMode = recipients.ExistingChats || recipients.NewChats ||
                                  recipients.Contacts || recipients.NonContacts ||
                                  (recipients.Users != null && recipients.Users.Count > 0);

            if (!hasExcludeMode && !hasIncludeMode)
            {
                RpcErrors.RpcErrors400.BusinessRecipientsEmpty.ThrowRpcError();
            }
        }

        // Generate connection ID
        var connectionId = Guid.NewGuid().ToString("N");

        // Convert recipients to BusinessBotRecipients
        var businessRecipients = new TBusinessBotRecipients();
        if (obj.Recipients is TInputBusinessBotRecipients inputRecipients)
        {
            businessRecipients.ExistingChats = inputRecipients.ExistingChats;
            businessRecipients.NewChats = inputRecipients.NewChats;
            businessRecipients.Contacts = inputRecipients.Contacts;
            businessRecipients.NonContacts = inputRecipients.NonContacts;
            businessRecipients.ExcludeSelected = inputRecipients.ExcludeSelected;

            // Convert TVector<IInputUser> to TVector<long>
            if (inputRecipients.Users != null && inputRecipients.Users.Count > 0)
            {
                var userIds = new TVector<long>();
                foreach (var u in inputRecipients.Users)
                {
                    if (u is TInputUser inputUser)
                    {
                        userIds.Add(inputUser.UserId);
                    }
                }
                businessRecipients.Users = userIds;
            }

            if (inputRecipients.ExcludeUsers != null && inputRecipients.ExcludeUsers.Count > 0)
            {
                var excludeIds = new TVector<long>();
                foreach (var u in inputRecipients.ExcludeUsers)
                {
                    if (u is TInputUser inputUser)
                    {
                        excludeIds.Add(inputUser.UserId);
                    }
                }
                businessRecipients.ExcludeUsers = excludeIds;
            }
        }

        // Store connection
        var connectionDoc = new BsonDocument
        {
            { "UserId", userId },
            { "BotId", botId },
            { "ConnectionId", connectionId },
            { "Recipients", businessRecipients.ToBsonDocument() },
            { "Rights", obj.Rights?.ToBsonDocument() ?? new TBusinessBotRights().ToBsonDocument() },
            { "CreatedAt", DateTime.UtcNow },
            { "UpdatedAt", DateTime.UtcNow }
        };

        var filter2 = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("UserId", userId),
            Builders<BsonDocument>.Filter.Eq("BotId", botId)
        );
        var options = new ReplaceOptions { IsUpsert = true };
        await collection.ReplaceOneAsync(filter2, connectionDoc, options);

        // Send updateBotBusinessConnect to bot
        var updateBotBusinessConnect = new TUpdateBotBusinessConnect
        {
            Connection = new TBotBusinessConnection
            {
                ConnectionId = connectionId,
                UserId = userId,
                DcId = 1,
                Date = CurrentDate,
                Disabled = false
            },
            Qts = await GetNextQtsAsync(botId)
        };

        // Get user info for bot
        var user = await _queryProcessor.ProcessAsync(new GetUserByIdQuery(userId));
        var tUser = user != null ? _userConverterService.ToUser(input, user, null, null, null, null, input.Layer) : null;

        var botUpdates = new TUpdates
        {
            Updates = new TVector<IUpdate> { updateBotBusinessConnect },
            Users = tUser != null ? new TVector<IUser> { tUser } : new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = CurrentDate
        };

        await _objectMessageSender.PushMessageToPeerAsync(
            new Peer(PeerType.User, botId),
            botUpdates,
            pts: 0
        );

        return new TUpdates
        {
            Updates = new TVector<IUpdate>(),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = CurrentDate
        };
    }

    private async Task<int> GetNextQtsAsync(long botId)
    {
        var countersCollection = _database.GetCollection<BsonDocument>("counters");
        var filter = Builders<BsonDocument>.Filter.Eq("_id", $"qts_{botId}");
        var update = Builders<BsonDocument>.Update.Inc("seq", 1);
        var options = new FindOneAndUpdateOptions<BsonDocument>
        {
            IsUpsert = true,
            ReturnDocument = ReturnDocument.After
        };
        var result = await countersCollection.FindOneAndUpdateAsync(filter, update, options);
        return result["seq"].AsInt32;
    }
}