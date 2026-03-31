using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Account;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Pause or unpause a specific chat, temporarily disconnecting it from all <a href="https://corefork.telegram.org/api/bots/connected-business-bots">business bots »</a>.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.toggleConnectedBotPaused"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ToggleConnectedBotPausedHandler : RpcResultObjectHandler<RequestToggleConnectedBotPaused, IBool>
{
    private readonly IMongoDatabase _database;
    private readonly IPeerHelper _peerHelper;

    public ToggleConnectedBotPausedHandler(IMongoDatabase database, IPeerHelper peerHelper)
    {
        _database = database;
        _peerHelper = peerHelper;
    }

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestToggleConnectedBotPaused obj)
    {
        var userId = input.UserId;
        var peer = _peerHelper.GetPeer(obj.Peer, userId);

        if (peer.PeerType != PeerType.User)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var collection = _database.GetCollection<BsonDocument>("paused_business_bot_chats");

        if (obj.Paused)
        {
            // Add to paused list
            var doc = new BsonDocument
            {
                { "UserId", userId },
                { "PeerId", peer.PeerId },
                { "PausedAt", DateTime.UtcNow }
            };

            var filter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("UserId", userId),
                Builders<BsonDocument>.Filter.Eq("PeerId", peer.PeerId)
            );

            await collection.ReplaceOneAsync(filter, doc, new ReplaceOptions { IsUpsert = true });
        }
        else
        {
            // Remove from paused list
            var filter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("UserId", userId),
                Builders<BsonDocument>.Filter.Eq("PeerId", peer.PeerId)
            );

            await collection.DeleteOneAsync(filter);
        }

        return new TBoolTrue();
    }
}