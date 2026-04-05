using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Change the <a href="https://corefork.telegram.org/api/reactions#notifications-about-reactions">reaction notification settings »</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.setReactionsNotifySettings"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// Layer 223: account.setReactionsNotifySettings#316ce548 settings:ReactionsNotifySettings = ReactionsNotifySettings;
/// </remarks>
internal sealed class SetReactionsNotifySettingsHandler(
    IMongoDatabase database,
    ILogger<SetReactionsNotifySettingsHandler> logger) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestSetReactionsNotifySettings, MyTelegram.Schema.IReactionsNotifySettings>
{
    protected override async Task<MyTelegram.Schema.IReactionsNotifySettings> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestSetReactionsNotifySettings obj)
    {
        if (obj.Settings is not MyTelegram.Schema.TReactionsNotifySettings)
        {
            RpcErrors.RpcErrors400.SettingsInvalid.ThrowRpcError();
        }

        var settings = (MyTelegram.Schema.TReactionsNotifySettings)obj.Settings;

        // Prepare settings document for MongoDB
        var settingsDoc = new BsonDocument
        {
            ["ShowPreviews"] = settings.ShowPreviews
        };

        // Store messages_notify_from
        if (settings.MessagesNotifyFrom != null)
        {
            settingsDoc["MessagesNotifyFrom"] = SerializeReactionNotificationsFrom(settings.MessagesNotifyFrom);
        }

        // Store stories_notify_from
        if (settings.StoriesNotifyFrom != null)
        {
            settingsDoc["StoriesNotifyFrom"] = SerializeReactionNotificationsFrom(settings.StoriesNotifyFrom);
        }

        // Store notification sound
        settingsDoc["Sound"] = SerializeNotificationSound(settings.Sound);

        // Save to MongoDB
        var collection = database.GetCollection<BsonDocument>("eventflow-userreadmodel");
        var filter = Builders<BsonDocument>.Filter.Eq("UserId", input.UserId);
        var update = Builders<BsonDocument>.Update.Set("ReactionsNotifySettings", settingsDoc);

        await collection.UpdateOneAsync(filter, update);

        logger.LogInformation("Reactions notify settings updated: UserId={UserId}, ShowPreviews={ShowPreviews}",
            input.UserId, settings.ShowPreviews);

        // Return the settings back to confirm
        return settings;
    }

    private static BsonDocument SerializeReactionNotificationsFrom(MyTelegram.Schema.IReactionNotificationsFrom notifyFrom)
    {
        return notifyFrom switch
        {
            MyTelegram.Schema.TReactionNotificationsFromAll => new BsonDocument { ["Type"] = "All" },
            MyTelegram.Schema.TReactionNotificationsFromContacts => new BsonDocument { ["Type"] = "Contacts" },
            _ => new BsonDocument { ["Type"] = "All" }
        };
    }

    private static BsonDocument SerializeNotificationSound(MyTelegram.Schema.INotificationSound sound)
    {
        return sound switch
        {
            MyTelegram.Schema.TNotificationSoundDefault => new BsonDocument { ["Type"] = "Default" },
            MyTelegram.Schema.TNotificationSoundNone => new BsonDocument { ["Type"] = "None" },
            MyTelegram.Schema.TNotificationSoundLocal local => new BsonDocument
            {
                ["Type"] = "Local",
                ["Title"] = local.Title,
                ["Data"] = local.Data
            },
            MyTelegram.Schema.TNotificationSoundRingtone ringtone => new BsonDocument
            {
                ["Type"] = "Ringtone",
                ["Id"] = ringtone.Id
            },
            _ => new BsonDocument { ["Type"] = "Default" }
        };
    }
}