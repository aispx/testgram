using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Account;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Permanently disconnect a specific chat from all <a href="https://corefork.telegram.org/api/bots/connected-business-bots">business bots »</a> (equivalent to specifying it in <code>recipients.exclude_users</code> during initial configuration with <a href="https://corefork.telegram.org/method/account.updateConnectedBot">account.updateConnectedBot »</a>); to reconnect of a chat disconnected using this method the user must reconnect the entire bot by invoking <a href="https://corefork.telegram.org/method/account.updateConnectedBot">account.updateConnectedBot »</a>.
/// Possible errors
/// Code Type Description
/// 400 BOT_ALREADY_DISABLED The connected business bot was already disabled for the specified peer.
/// 400 BOT_NOT_CONNECTED_YET No <a href="https://corefork.telegram.org/api/business#connected-bots">business bot</a> is connected to the currently logged in user.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.disablePeerConnectedBot"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class DisablePeerConnectedBotHandler : RpcResultObjectHandler<RequestDisablePeerConnectedBot, IBool>
{
    private readonly IMongoDatabase _database;
    private readonly IPeerHelper _peerHelper;

    public DisablePeerConnectedBotHandler(IMongoDatabase database, IPeerHelper peerHelper)
    {
        _database = database;
        _peerHelper = peerHelper;
    }

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestDisablePeerConnectedBot obj)
    {
        var userId = input.UserId;
        var peer = _peerHelper.GetPeer(obj.Peer, userId);

        if (peer.PeerId == 0)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var collection = _database.GetCollection<BsonDocument>("connected_business_bots");
        var filter = Builders<BsonDocument>.Filter.Eq("UserId", userId);
        var connection = await collection.Find(filter).FirstOrDefaultAsync();

        if (connection == null)
        {
            RpcErrors.RpcErrors400.BotNotConnectedYet.ThrowRpcError();
        }

        // Check if already disabled
        var recipientsDoc = connection["Recipients"].AsBsonDocument;
        if (recipientsDoc.Contains("ExcludeUsers"))
        {
            var excludeArray = recipientsDoc["ExcludeUsers"].AsBsonArray;
            if (excludeArray.Any(u => u.AsInt64 == peer.PeerId))
            {
                RpcErrors.RpcErrors400.BotAlreadyDisabled.ThrowRpcError();
            }
        }

        // Add peer to ExcludeUsers
        var update = Builders<BsonDocument>.Update.AddToSet("Recipients.ExcludeUsers", peer.PeerId);
        await collection.UpdateOneAsync(filter, update);

        return new TBoolTrue();
    }
}