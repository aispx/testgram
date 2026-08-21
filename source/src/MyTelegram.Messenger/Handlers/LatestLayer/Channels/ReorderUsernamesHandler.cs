using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;

/// <summary>
/// Reorder usernames associated with a channel
/// See https://core.telegram.org/method/channels.reorderUsernames
/// </summary>
internal sealed class ReorderUsernamesHandler : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestReorderUsernames, IBool>
{
    private readonly IMongoDatabase _database;
    private readonly IAccessHashHelper2 _accessHashHelper;
    private readonly IChannelAdminRightsChecker _channelAdminRightsChecker;
    private readonly IChannelUpdateNotifier _channelUpdateNotifier;

    public ReorderUsernamesHandler(IMongoDatabase database,
        IAccessHashHelper2 accessHashHelper,
        IChannelAdminRightsChecker channelAdminRightsChecker,
        IChannelUpdateNotifier channelUpdateNotifier)
    {
        _database = database;
        _accessHashHelper = accessHashHelper;
        _channelAdminRightsChecker = channelAdminRightsChecker;
        _channelUpdateNotifier = channelUpdateNotifier;
    }

    protected override async Task<IBool> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Channels.RequestReorderUsernames obj)
    {
        if (obj.Channel is not TInputChannel inputChannel)
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
            return null!;
        }

        await _accessHashHelper.CheckAccessHashAsync(input, inputChannel);
        await _channelAdminRightsChecker.ThrowIfNotChannelOwnerAsync(inputChannel.ChannelId, input.UserId);

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
        var usernamesV2 = channel!.Contains("Usernames") && !channel["Usernames"].IsBsonNull
            ? channel["Usernames"].AsBsonArray
            : new BsonArray();

        // Build username map
        var usernameMap = new Dictionary<string, BsonDocument>(StringComparer.OrdinalIgnoreCase);
        var activeUsernames = new List<string>();
        
        foreach (var item in usernamesV2)
        {
            if (item.IsBsonDocument)
            {
                var doc = item.AsBsonDocument;
                if (doc.Contains("Username"))
                {
                    var username = doc["Username"].AsString;
                    usernameMap[username] = doc;
                    
                    if (doc.Contains("Active") && doc["Active"].AsBoolean)
                    {
                        activeUsernames.Add(username.ToLower());
                    }
                }
            }
        }

        // Validate order contains all active usernames
        var orderLower = obj.Order.Select(u => u.ToLower()).ToList();
        var activeSet = new HashSet<string>(activeUsernames);
        var orderSet = new HashSet<string>(orderLower);

        if (!activeSet.SetEquals(orderSet))
        {
            RpcErrors.RpcErrors400.OrderInvalid.ThrowRpcError();
            return null!;
        }

        // Reorder usernames
        var reorderedUsernames = new BsonArray();
        
        foreach (var username in obj.Order)
        {
            if (usernameMap.TryGetValue(username, out var doc))
            {
                reorderedUsernames.Add(doc);
            }
        }
        
        foreach (var item in usernamesV2)
        {
            if (item.IsBsonDocument)
            {
                var doc = item.AsBsonDocument;
                if (doc.Contains("Username"))
                {
                    var username = doc["Username"].AsString;
                    var isActive = doc.Contains("Active") && doc["Active"].AsBoolean;
                    
                    if (!isActive)
                    {
                        reorderedUsernames.Add(doc);
                    }
                }
            }
        }

        // Save back to MongoDB
        var update = Builders<BsonDocument>.Update.Set("Usernames", reorderedUsernames);
        await channelCollection.UpdateOneAsync(channelFilter, update);

        await AdminLogHelper.LogChangeUsernames(_database, channelId, input.UserId,
            AdminLogHelper.ActiveUsernames(usernamesV2), AdminLogHelper.ActiveUsernames(reorderedUsernames));

        await _channelUpdateNotifier.NotifyChannelChangedAsync(input, channelId);

        return new TBoolTrue();
    }
}
