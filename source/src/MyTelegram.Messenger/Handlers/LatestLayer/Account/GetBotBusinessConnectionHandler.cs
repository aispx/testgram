using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Account;
using MyTelegram.Converters.Mappers.LatestLayer;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Bots may invoke this method to re-fetch the <a href="https://corefork.telegram.org/constructor/updateBotBusinessConnect">updateBotBusinessConnect</a> constructor associated with a specific <a href="https://corefork.telegram.org/api/bots/connected-business-bots">business <code>connection_id</code>, see here »</a> for more info on connected business bots.<br/>
/// This is needed for example for freshly logged in bots that are receiving some <a href="https://corefork.telegram.org/constructor/updateBotNewBusinessMessage">updateBotNewBusinessMessage</a>, etc. updates because some users have already connected to the bot before it could login.<br/>
/// In this case, the bot is receiving messages from the business connection, but it hasn't cached the associated <a href="https://corefork.telegram.org/constructor/updateBotBusinessConnect">updateBotBusinessConnect</a> with info about the connection (can it reply to messages? etc.) yet, and cannot receive the old ones because they were sent when the bot wasn't logged into the session yet.<br/>
/// This method can be used to fetch info about a not-yet-cached business connection, and should not be invoked if the info is already cached or to fetch changes, as eventual changes will automatically be sent as new <a href="https://corefork.telegram.org/constructor/updateBotBusinessConnect">updateBotBusinessConnect</a> updates to the bot using the usual <a href="https://corefork.telegram.org/api/updates">update delivery methods »</a>.
/// Possible errors
/// Code Type Description
/// 400 CONNECTION_ID_INVALID The specified connection ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getBotBusinessConnection"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class GetBotBusinessConnectionHandler : RpcResultObjectHandler<RequestGetBotBusinessConnection, IUpdates>
{
    private readonly IMongoDatabase _database;
    private readonly IQueryProcessor _queryProcessor;
    private readonly IUserConverterService _userConverterService;

    public GetBotBusinessConnectionHandler(IMongoDatabase database, IQueryProcessor queryProcessor, IUserConverterService userConverterService)
    {
        _database = database;
        _queryProcessor = queryProcessor;
        _userConverterService = userConverterService;
    }

    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestGetBotBusinessConnection obj)
    {
        var botId = input.UserId;
        var connectionId = obj.ConnectionId;

        var collection = _database.GetCollection<BsonDocument>("connected_business_bots");
        var filter = Builders<BsonDocument>.Filter.Eq("ConnectionId", connectionId);
        var connection = await collection.Find(filter).FirstOrDefaultAsync();

        if (connection == null)
        {
            RpcErrors.RpcErrors400.ConnectionIdInvalid.ThrowRpcError();
        }

        var userId = connection["UserId"].AsInt64;
        var connBotId = connection["BotId"].AsInt64;

        // Verify this bot is the connected bot
        if (connBotId != botId)
        {
            RpcErrors.RpcErrors400.ConnectionIdInvalid.ThrowRpcError();
        }

        // Get user
        var user = await _queryProcessor.ProcessAsync(new GetUserByIdQuery(userId));
        if (user == null)
        {
            RpcErrors.RpcErrors400.ConnectionIdInvalid.ThrowRpcError();
        }

        // Parse connection data
        var recipientsDoc = connection["Recipients"].AsBsonDocument;
        var recipients = new TBusinessBotRecipients
        {
            ExistingChats = recipientsDoc.Contains("ExistingChats") && recipientsDoc["ExistingChats"].AsBoolean,
            NewChats = recipientsDoc.Contains("NewChats") && recipientsDoc["NewChats"].AsBoolean,
            Contacts = recipientsDoc.Contains("Contacts") && recipientsDoc["Contacts"].AsBoolean,
            NonContacts = recipientsDoc.Contains("NonContacts") && recipientsDoc["NonContacts"].AsBoolean,
            ExcludeSelected = recipientsDoc.Contains("ExcludeSelected") && recipientsDoc["ExcludeSelected"].AsBoolean
        };

        if (recipientsDoc.Contains("Users"))
        {
            var usersArray = recipientsDoc["Users"].AsBsonArray;
            recipients.Users = new TVector<long>(usersArray.Select(u => u.AsInt64));
        }

        if (recipientsDoc.Contains("ExcludeUsers"))
        {
            var excludeArray = recipientsDoc["ExcludeUsers"].AsBsonArray;
            recipients.ExcludeUsers = new TVector<long>(excludeArray.Select(u => u.AsInt64));
        }

        var rightsDoc = connection["Rights"].AsBsonDocument;
        var rights = new TBusinessBotRights
        {
            Reply = rightsDoc.Contains("Reply") && rightsDoc["Reply"].AsBoolean,
            ReadMessages = rightsDoc.Contains("ReadMessages") && rightsDoc["ReadMessages"].AsBoolean,
            DeleteSentMessages = rightsDoc.Contains("DeleteSentMessages") && rightsDoc["DeleteSentMessages"].AsBoolean,
            DeleteReceivedMessages = rightsDoc.Contains("DeleteReceivedMessages") && rightsDoc["DeleteReceivedMessages"].AsBoolean,
            EditName = rightsDoc.Contains("EditName") && rightsDoc["EditName"].AsBoolean,
            EditBio = rightsDoc.Contains("EditBio") && rightsDoc["EditBio"].AsBoolean,
            EditProfilePhoto = rightsDoc.Contains("EditProfilePhoto") && rightsDoc["EditProfilePhoto"].AsBoolean,
            EditUsername = rightsDoc.Contains("EditUsername") && rightsDoc["EditUsername"].AsBoolean,
            ViewGifts = rightsDoc.Contains("ViewGifts") && rightsDoc["ViewGifts"].AsBoolean,
            SellGifts = rightsDoc.Contains("SellGifts") && rightsDoc["SellGifts"].AsBoolean,
            ChangeGiftSettings = rightsDoc.Contains("ChangeGiftSettings") && rightsDoc["ChangeGiftSettings"].AsBoolean,
            TransferAndUpgradeGifts = rightsDoc.Contains("TransferAndUpgradeGifts") && rightsDoc["TransferAndUpgradeGifts"].AsBoolean,
            TransferStars = rightsDoc.Contains("TransferStars") && rightsDoc["TransferStars"].AsBoolean,
            ManageStories = rightsDoc.Contains("ManageStories") && rightsDoc["ManageStories"].AsBoolean
        };

        var updateBotBusinessConnect = new TUpdateBotBusinessConnect
        {
            Connection = new TBotBusinessConnection
            {
                ConnectionId = connectionId,
                UserId = userId,
                DcId = 1,
                Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Rights = rights
            },
            Qts = await GetNextQtsAsync(botId)
        };

        // Convert IUserReadModel to IUser
        var tUser = _userConverterService.ToUser(input, user, null, null, null, null, input.Layer);

        return new TUpdates
        {
            Updates = new TVector<IUpdate> { updateBotBusinessConnect },
            Users = new TVector<IUser> { tUser },
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