using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;

/// <summary>
/// Deactivate all collectible usernames for a channel
/// See https://core.telegram.org/method/channels.deactivateAllUsernames
/// </summary>
internal sealed class DeactivateAllUsernamesHandler : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestDeactivateAllUsernames, IBool>
{
    private readonly IMongoDatabase _database;

    public DeactivateAllUsernamesHandler(IMongoDatabase database)
    {
        _database = database;
    }

    protected override async Task<IBool> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Channels.RequestDeactivateAllUsernames obj)
    {
        if (obj.Channel is not TInputChannel inputChannel)
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
            return null!;
        }

        var channelId = inputChannel.ChannelId;

        // Get channel from MongoDB
        var channelCollection = _database.GetCollection<BsonDocument>("eventflow-channelreadmodel");
        var channelFilter = Builders<BsonDocument>.Filter.Eq("ChannelId", channelId);
        var channel = await channelCollection.Find(channelFilter).FirstOrDefaultAsync();

        if (channel == null)
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
            return null!;
        }

        // Get current usernames
        var usernamesV2 = channel.Contains("UsernamesV2") && !channel["UsernamesV2"].IsBsonNull
            ? channel["UsernamesV2"].AsBsonArray
            : new BsonArray();

        // Deactivate all non-editable (Fragment) usernames
        foreach (var item in usernamesV2)
        {
            if (item.IsBsonDocument)
            {
                var doc = item.AsBsonDocument;
                var isEditable = doc.Contains("Editable") && doc["Editable"].AsBoolean;
                
                // Only deactivate Fragment usernames (non-editable)
                if (!isEditable)
                {
                    doc["Active"] = false;
                }
            }
        }

        // Save back to MongoDB
        var update = Builders<BsonDocument>.Update.Set("UsernamesV2", usernamesV2);
        await channelCollection.UpdateOneAsync(channelFilter, update);

        return new TBoolTrue();
    }
}
