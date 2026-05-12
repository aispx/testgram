using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Deletes messages from a <a href="https://corefork.telegram.org/api/monoforum">monoforum topic »</a>, or deletes messages forwarded from a specific peer to <a href="https://corefork.telegram.org/api/saved-messages">saved messages »</a>.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.deleteSavedHistory"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class DeleteSavedHistoryHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestDeleteSavedHistory, MyTelegram.Schema.Messages.IAffectedHistory>
{
    protected override async Task<MyTelegram.Schema.Messages.IAffectedHistory> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestDeleteSavedHistory obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        long ownerPeerId;
        Peer savedPeer;
        if (obj.ParentPeer != null)
        {
            var parentPeer = peerHelper.GetPeer(obj.ParentPeer, input.UserId);
            if (parentPeer == null || parentPeer.PeerType != PeerType.Channel || peer!.PeerType != PeerType.User)
            {
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            }

            var monoforum = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(parentPeer.PeerId));
            if (monoforum == null || !monoforum.IsMonoforum)
            {
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            }

            ownerPeerId = parentPeer.PeerId;
            savedPeer = peer;
        }
        else
        {
            ownerPeerId = input.UserId;
            savedPeer = peer!;
        }

        var filterBuilder = Builders<BsonDocument>.Filter;
        var filter = filterBuilder.And(
            filterBuilder.Eq("OwnerPeerId", ownerPeerId),
            filterBuilder.Eq("SavedPeerId.PeerType", savedPeer.PeerType),
            filterBuilder.Eq("SavedPeerId.PeerId", savedPeer.PeerId)
        );

        if (obj.MaxId > 0)
        {
            filter &= filterBuilder.Lte("MessageId", obj.MaxId);
        }

        if (obj.MinDate.HasValue)
        {
            filter &= filterBuilder.Gte("Date", obj.MinDate.Value);
        }

        if (obj.MaxDate.HasValue)
        {
            filter &= filterBuilder.Lte("Date", obj.MaxDate.Value);
        }

        var messagesCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-messagereadmodel");
        var result = await messagesCol.DeleteManyAsync(filter);

        return new TAffectedHistory
        {
            Pts = 0,
            PtsCount = (int)result.DeletedCount,
            Offset = 0
        };
    }
}
