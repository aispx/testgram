using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Account;
using MyTelegram.Converters.Mappers.LatestLayer;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// List all currently connected <a href="https://corefork.telegram.org/api/bots/connected-business-bots">business bots »</a>
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getConnectedBots"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetConnectedBotsHandler : RpcResultObjectHandler<RequestGetConnectedBots, IConnectedBots>
{
    private readonly IMongoDatabase _database;
    private readonly IQueryProcessor _queryProcessor;
    private readonly IUserConverterService _userConverterService;

    public GetConnectedBotsHandler(IMongoDatabase database, IQueryProcessor queryProcessor, IUserConverterService userConverterService)
    {
        _database = database;
        _queryProcessor = queryProcessor;
        _userConverterService = userConverterService;
    }

    protected override async Task<IConnectedBots> HandleCoreAsync(IRequestInput input, RequestGetConnectedBots obj)
    {
        var userId = input.UserId;
        var collection = _database.GetCollection<BsonDocument>("connected_business_bots");

        var filter = Builders<BsonDocument>.Filter.Eq("UserId", userId);
        var connections = await collection.Find(filter).ToListAsync();

        var connectedBots = new TVector<IConnectedBot>();
        var users = new TVector<IUser>();

        foreach (var conn in connections)
        {
            var botId = conn["BotId"].AsInt64;

            // Get bot user
            var botUser = await _queryProcessor.ProcessAsync(new GetUserByIdQuery(botId));
            if (botUser != null)
            {
                var tUser = _userConverterService.ToUser(input, botUser, null, null, null, null, input.Layer);
                users.Add(tUser);
            }

            // Parse recipients
            var recipientsDoc = conn["Recipients"].AsBsonDocument;
            var recipients = new TBusinessBotRecipients
            {
                ExistingChats = recipientsDoc.Contains("ExistingChats") && recipientsDoc["ExistingChats"].AsBoolean,
                NewChats = recipientsDoc.Contains("NewChats") && recipientsDoc["NewChats"].AsBoolean,
                Contacts = recipientsDoc.Contains("Contacts") && recipientsDoc["Contacts"].AsBoolean,
                NonContacts = recipientsDoc.Contains("NonContacts") && recipientsDoc["NonContacts"].AsBoolean,
                ExcludeSelected = recipientsDoc.Contains("ExcludeSelected") && recipientsDoc["ExcludeSelected"].AsBoolean
            };

            if (recipientsDoc.Contains("Users") && !recipientsDoc["Users"].IsBsonNull)
            {
                var usersArray = recipientsDoc["Users"].AsBsonArray;
                recipients.Users = new TVector<long>(usersArray.Select(u => u.AsInt64));
            }

            if (recipientsDoc.Contains("ExcludeUsers") && !recipientsDoc["ExcludeUsers"].IsBsonNull)
            {
                var excludeArray = recipientsDoc["ExcludeUsers"].AsBsonArray;
                recipients.ExcludeUsers = new TVector<long>(excludeArray.Select(u => u.AsInt64));
            }

            // Parse rights
            var rightsDoc = conn["Rights"].AsBsonDocument;
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

            connectedBots.Add(new TConnectedBot
            {
                BotId = botId,
                Recipients = recipients,
                Rights = rights
            });
        }

        return new TConnectedBots
        {
            ConnectedBots = connectedBots,
            Users = users
        };
    }
}