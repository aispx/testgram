using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Schema.Account;
using TBusinessGreetingMessage = MyTelegram.Schema.TBusinessGreetingMessage;
using TBusinessRecipients = MyTelegram.Schema.TBusinessRecipients;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

internal sealed class UpdateBusinessGreetingMessageHandler : RpcResultObjectHandler<RequestUpdateBusinessGreetingMessage, IBool>
{
    private readonly IMongoDatabase _database;
    private readonly IUserAppService _userAppService;

    public UpdateBusinessGreetingMessageHandler(IMongoDatabase database, IUserAppService userAppService)
    {
        _database = database;
        _userAppService = userAppService;
    }

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestUpdateBusinessGreetingMessage obj)
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

            var greetingMessage = new TBusinessGreetingMessage
            {
                ShortcutId = msg.ShortcutId,
                Recipients = recipients,
                NoActivityDays = msg.NoActivityDays
            };

            var update = Builders<BsonDocument>.Update.Set("BusinessGreetingMessage", greetingMessage.ToBsonDocument());
            await collection.UpdateOneAsync(filter, update);
        }
        else
        {
            var update = Builders<BsonDocument>.Update.Unset("BusinessGreetingMessage");
            await collection.UpdateOneAsync(filter, update);
        }

        _userAppService.InvalidateCache(userId);

        return new TBoolTrue();
    }
}
