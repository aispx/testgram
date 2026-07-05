using MongoDB.Bson;
using MongoDB.Driver;
namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
internal sealed class SetChatAvailableReactionsHandler(
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSetChatAvailableReactions, MyTelegram.Schema.IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestSetChatAvailableReactions obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);

        if (obj.PaidEnabled.HasValue && peer.PeerType == PeerType.Channel)
        {
            var enabled = obj.PaidEnabled.Value;
            await mongoDatabase.GetCollection<BsonDocument>("eventflow-channelreadmodel")
                .UpdateOneAsync(
                    Builders<BsonDocument>.Filter.Eq("ChannelId", peer.PeerId),
                    Builders<BsonDocument>.Update.Set("PaidReactionsEnabled", enabled));
        }

        return new TUpdates { Updates = new(), Chats = new(), Users = new(), Date = CurrentDate };
    }
}
