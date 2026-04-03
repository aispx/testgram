using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;

/// <summary>
/// Activate or deactivate a purchased fragment.com username for a channel
/// See https://core.telegram.org/method/channels.toggleUsername
/// </summary>
internal sealed class ToggleUsernameHandler : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestToggleUsername, IBool>
{
    private readonly IMongoDatabase _database;

    public ToggleUsernameHandler(IMongoDatabase database)
    {
        _database = database;
    }

    protected override async Task<IBool> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Channels.RequestToggleUsername obj)
    {
        if (obj.Channel is not TInputChannel inputChannel)
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
            return null!;
        }

        var channelId = inputChannel.ChannelId;
        var username = obj.Username.ToLower();

        // Get channel from MongoDB
        var channelCollection = _database.GetCollection<BsonDocument>("eventflow-channelreadmodel");
        var channelFilter = Builders<BsonDocument>.Filter.Eq("ChannelId", channelId);
        var channel = await channelCollection.Find(channelFilter).FirstOrDefaultAsync();

        if (channel == null)
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
            return null!;
        }

        // Get current usernames from Usernames
        var usernamesV2 = channel.Contains("Usernames") && !channel["Usernames"].IsBsonNull
            ? channel["Usernames"].AsBsonArray
            : new BsonArray();

        if (usernamesV2.Count == 0)
        {
            RpcErrors.RpcErrors400.UsernameInvalid.ThrowRpcError();
            return null!;
        }

        // Find the username
        BsonDocument? targetUsername = null;
        int targetIndex = -1;
        for (int i = 0; i < usernamesV2.Count; i++)
        {
            if (usernamesV2[i].IsBsonDocument)
            {
                var doc = usernamesV2[i].AsBsonDocument;
                if (doc.Contains("Username") && doc["Username"].AsString.ToLower() == username)
                {
                    targetUsername = doc;
                    targetIndex = i;
                    break;
                }
            }
        }

        if (targetUsername == null)
        {
            RpcErrors.RpcErrors400.UsernameInvalid.ThrowRpcError();
            return null!;
        }

        // Check if it's editable (basic username cannot be deactivated)
        var isEditable = targetUsername.Contains("Editable") && targetUsername["Editable"].AsBoolean;
        if (isEditable && !obj.Active)
        {
            RpcErrors.RpcErrors400.UsernameNotModified.ThrowRpcError();
            return null!;
        }

        // Check current active state
        var currentActive = targetUsername.Contains("Active") && targetUsername["Active"].AsBoolean;
        if (currentActive == obj.Active)
        {
            RpcErrors.RpcErrors400.UsernameNotModified.ThrowRpcError();
            return null!;
        }

        // Count active usernames
        var activeCount = 0;
        foreach (var item in usernamesV2)
        {
            if (item.IsBsonDocument)
            {
                var doc = item.AsBsonDocument;
                if (doc.Contains("Active") && doc["Active"].AsBoolean)
                {
                    activeCount++;
                }
            }
        }

        // Check max active usernames (10)
        if (obj.Active && activeCount >= 10)
        {
            RpcErrors.RpcErrors400.UsernamesActiveTooMuch.ThrowRpcError();
            return null!;
        }

        // Update username active state
        targetUsername["Active"] = obj.Active;
        usernamesV2[targetIndex] = targetUsername;

        // Save back to MongoDB
        var update = Builders<BsonDocument>.Update.Set("Usernames", usernamesV2);
        await channelCollection.UpdateOneAsync(channelFilter, update);

        return new TBoolTrue();
    }
}
