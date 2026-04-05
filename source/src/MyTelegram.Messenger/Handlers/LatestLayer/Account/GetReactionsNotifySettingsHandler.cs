using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Get the current <a href="https://corefork.telegram.org/api/reactions#notifications-about-reactions">reaction notification settings »</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getReactionsNotifySettings"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// Layer 223: account.getReactionsNotifySettings#6dd654c = ReactionsNotifySettings;
/// </remarks>
internal sealed class GetReactionsNotifySettingsHandler(
    IMongoDatabase database) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetReactionsNotifySettings, MyTelegram.Schema.IReactionsNotifySettings>
{
    protected override async Task<MyTelegram.Schema.IReactionsNotifySettings> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestGetReactionsNotifySettings obj)
    {
        // Get user settings from MongoDB
        var collection = database.GetCollection<BsonDocument>("eventflow-userreadmodel");
        var filter = Builders<BsonDocument>.Filter.Eq("UserId", input.UserId);
        var user = await collection.Find(filter).FirstOrDefaultAsync();

        // Default settings
        var settings = new MyTelegram.Schema.TReactionsNotifySettings
        {
            ShowPreviews = true,
            Sound = new TNotificationSoundDefault()
        };

        if (user != null && user.Contains("ReactionsNotifySettings") && user["ReactionsNotifySettings"].IsBsonDocument)
        {
            var settingsDoc = user["ReactionsNotifySettings"].AsBsonDocument;

            // Read ShowPreviews
            if (settingsDoc.Contains("ShowPreviews"))
            {
                settings.ShowPreviews = settingsDoc["ShowPreviews"].AsBoolean;
            }

            // Read MessagesNotifyFrom
            if (settingsDoc.Contains("MessagesNotifyFrom") && settingsDoc["MessagesNotifyFrom"].IsBsonDocument)
            {
                settings.MessagesNotifyFrom = DeserializeReactionNotificationsFrom(settingsDoc["MessagesNotifyFrom"].AsBsonDocument);
            }

            // Read StoriesNotifyFrom
            if (settingsDoc.Contains("StoriesNotifyFrom") && settingsDoc["StoriesNotifyFrom"].IsBsonDocument)
            {
                settings.StoriesNotifyFrom = DeserializeReactionNotificationsFrom(settingsDoc["StoriesNotifyFrom"].AsBsonDocument);
            }

            // Read Sound
            if (settingsDoc.Contains("Sound") && settingsDoc["Sound"].IsBsonDocument)
            {
                settings.Sound = DeserializeNotificationSound(settingsDoc["Sound"].AsBsonDocument);
            }
        }

        return settings;
    }

    private static MyTelegram.Schema.IReactionNotificationsFrom? DeserializeReactionNotificationsFrom(BsonDocument doc)
    {
        if (!doc.Contains("Type"))
        {
            return null;
        }

        var type = doc["Type"].AsString;
        return type switch
        {
            "All" => new MyTelegram.Schema.TReactionNotificationsFromAll(),
            "Contacts" => new MyTelegram.Schema.TReactionNotificationsFromContacts(),
            _ => null
        };
    }

    private static MyTelegram.Schema.INotificationSound DeserializeNotificationSound(BsonDocument doc)
    {
        if (!doc.Contains("Type"))
        {
            return new TNotificationSoundDefault();
        }

        var type = doc["Type"].AsString;
        return type switch
        {
            "None" => new MyTelegram.Schema.TNotificationSoundNone(),
            "Local" => new MyTelegram.Schema.TNotificationSoundLocal
            {
                Title = doc.Contains("Title") ? doc["Title"].AsString : "",
                Data = doc.Contains("Data") ? doc["Data"].AsString : ""
            },
            "Ringtone" => new MyTelegram.Schema.TNotificationSoundRingtone
            {
                Id = doc.Contains("Id") ? doc["Id"].AsInt64 : 0
            },
            _ => new TNotificationSoundDefault()
        };
    }
}