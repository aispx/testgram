using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Schema.Account;
using TBusinessAwayMessage = MyTelegram.Schema.TBusinessAwayMessage;
using TBusinessRecipients = MyTelegram.Schema.TBusinessRecipients;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

internal sealed class UpdateBusinessAwayMessageHandler : RpcResultObjectHandler<RequestUpdateBusinessAwayMessage, IBool>
{
    private readonly IMongoDatabase _database;
    private readonly IUserAppService _userAppService;

    public UpdateBusinessAwayMessageHandler(IMongoDatabase database, IUserAppService userAppService)
    {
        _database = database;
        _userAppService = userAppService;
    }

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestUpdateBusinessAwayMessage obj)
    {
        var userId = input.UserId;
        var collection = _database.GetCollection<BsonDocument>("eventflow-userreadmodel");
        var filter = Builders<BsonDocument>.Filter.Eq("UserId", userId);

        if (obj.Flags.IsBitSet(0) && obj.Message != null)
        {
            var msg = obj.Message;
            var recipients = new TBusinessRecipients
            {
                Flags = msg.Recipients.Flags,
                ExistingChats = msg.Recipients.ExistingChats,
                NewChats = msg.Recipients.NewChats,
                Contacts = msg.Recipients.Contacts,
                NonContacts = msg.Recipients.NonContacts,
                ExcludeSelected = msg.Recipients.ExcludeSelected
            };

            var awayMessage = new TBusinessAwayMessage
            {
                Flags = msg.Flags,
                OfflineOnly = msg.OfflineOnly,
                ShortcutId = msg.ShortcutId,
                Schedule = msg.Schedule,
                Recipients = recipients
            };

            var update = Builders<BsonDocument>.Update.Set("BusinessAwayMessage", awayMessage.ToBsonDocument());
            await collection.UpdateOneAsync(filter, update);
        }
        else
        {
            var update = Builders<BsonDocument>.Update.Unset("BusinessAwayMessage");
            await collection.UpdateOneAsync(filter, update);
        }

        _userAppService.InvalidateCache(userId);

        return new TBoolTrue();
    }
}
