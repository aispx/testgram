using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Chatlists;
/// <summary>
/// Edit a <a href="https://corefork.telegram.org/api/links#chat-folder-links">chat folder deep link »</a>.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 FILTER_ID_INVALID The specified filter ID is invalid.
/// 400 FILTER_NOT_SUPPORTED The specified filter cannot be used in this context.
/// 400 INVITE_SLUG_EMPTY The specified invite slug is empty.
/// 400 INVITE_SLUG_EXPIRED The specified chat folder link has expired.
/// 400 PEERS_LIST_EMPTY The specified list of peers is empty.
/// <para><c>See <a href="https://corefork.telegram.org/method/chatlists.editExportedInvite"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class EditExportedInviteHandler : RpcResultObjectHandler<MyTelegram.Schema.Chatlists.RequestEditExportedInvite, MyTelegram.Schema.IExportedChatlistInvite>
{
    private readonly IMongoDatabase _database;
    private readonly IPeerHelper _peerHelper;

    public EditExportedInviteHandler(
        IMongoDatabase database,
        IPeerHelper peerHelper)
    {
        _database = database;
        _peerHelper = peerHelper;
    }

    protected override async Task<MyTelegram.Schema.IExportedChatlistInvite> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Chatlists.RequestEditExportedInvite obj)
    {
        // 1. Validate chatlist
        if (obj.Chatlist is not TInputChatlistDialogFilter chatlistFilter)
        {
            RpcErrors.RpcErrors400.FilterIdInvalid.ThrowRpcError();
            return null!;
        }

        // 2. Find invite
        var collection = _database.GetCollection<BsonDocument>("chatlist_invites");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("Slug", obj.Slug),
            Builders<BsonDocument>.Filter.Eq("CreatorUserId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("FilterId", chatlistFilter.FilterId)
        );

        var inviteDoc = await collection.Find(filter).FirstOrDefaultAsync();
        if (inviteDoc == null)
        {
            RpcErrors.RpcErrors400.InviteSlugExpired.ThrowRpcError();
        }

        // 3. Build update
        var updateBuilder = Builders<BsonDocument>.Update;
        var updates = new List<UpdateDefinition<BsonDocument>>();

        if (obj.Title != null)
        {
            updates.Add(updateBuilder.Set("Title", obj.Title));
        }

        if (obj.Peers != null)
        {
            var peerIds = new List<long>();
            var peerTypes = new List<string>();

            foreach (var inputPeer in obj.Peers)
            {
                var peer = _peerHelper.GetPeer(inputPeer, input.UserId);
                peerIds.Add(peer.PeerId);
                peerTypes.Add(peer.PeerType.ToString());
            }

            updates.Add(updateBuilder.Set("PeerIds", new BsonArray(peerIds)));
            updates.Add(updateBuilder.Set("PeerTypes", new BsonArray(peerTypes)));
        }

        if (updates.Count > 0)
        {
            await collection.UpdateOneAsync(filter, updateBuilder.Combine(updates));
        }

        // 4. Fetch updated document
        inviteDoc = await collection.Find(filter).FirstOrDefaultAsync();

        // 5. Build response
        var title = inviteDoc["Title"].AsString;
        var peerIdsList = inviteDoc["PeerIds"].AsBsonArray.Select(p => p.AsInt64).ToList();
        var peerTypesList = inviteDoc["PeerTypes"].AsBsonArray.Select(p => p.AsString).ToList();

        var peers = new TVector<IPeer>();
        for (int i = 0; i < peerIdsList.Count; i++)
        {
            var peerType = Enum.Parse<PeerType>(peerTypesList[i]);
            peers.Add(new Peer(peerType, peerIdsList[i]).ToPeer());
        }

        return new MyTelegram.Schema.TExportedChatlistInvite
        {
            Title = title,
            Url = $"https://t.me/addlist/{obj.Slug}",
            Peers = peers
        };
    }
}