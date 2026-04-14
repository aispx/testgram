using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Chatlists;
/// <summary>
/// List all <a href="https://corefork.telegram.org/api/links#chat-folder-links">chat folder deep links »</a> associated to a folder
/// Possible errors
/// Code Type Description
/// 400 FILTER_ID_INVALID The specified filter ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/chatlists.getExportedInvites"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetExportedInvitesHandler : RpcResultObjectHandler<MyTelegram.Schema.Chatlists.RequestGetExportedInvites, MyTelegram.Schema.Chatlists.IExportedInvites>
{
    private readonly IMongoDatabase _database;

    public GetExportedInvitesHandler(IMongoDatabase database)
    {
        _database = database;
    }

    protected override async Task<MyTelegram.Schema.Chatlists.IExportedInvites> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Chatlists.RequestGetExportedInvites obj)
    {
        // 1. Validate chatlist
        if (obj.Chatlist is not TInputChatlistDialogFilter chatlistFilter)
        {
            return RpcErrors.RpcErrors400.FilterIdInvalid.ThrowRpcError<MyTelegram.Schema.Chatlists.IExportedInvites>();
        }

        var filterId = chatlistFilter.FilterId;

        // 2. Find all invites for this filter
        var collection = _database.GetCollection<BsonDocument>("chatlist_invites");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("CreatorUserId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("FilterId", filterId),
            Builders<BsonDocument>.Filter.Eq("Revoked", false)
        );

        var inviteDocs = await collection.Find(filter).ToListAsync();

        // 3. Build response
        var invites = new TVector<IExportedChatlistInvite>();

        foreach (var doc in inviteDocs)
        {
            var slug = doc["Slug"].AsString;
            var title = doc["Title"].AsString;
            var peerIds = doc["PeerIds"].AsBsonArray.Select(p => p.AsInt64).ToList();
            var peerTypes = doc["PeerTypes"].AsBsonArray.Select(p => p.AsString).ToList();

            var peers = new TVector<IPeer>();
            for (int i = 0; i < peerIds.Count; i++)
            {
                var peerType = Enum.Parse<PeerType>(peerTypes[i]);
                peers.Add(new Peer(peerType, peerIds[i]).ToPeer());
            }

            invites.Add(new MyTelegram.Schema.TExportedChatlistInvite
            {
                Title = title,
                Url = $"https://t.me/addlist/{slug}",
                Peers = peers
            });
        }

        return new MyTelegram.Schema.Chatlists.TExportedInvites
        {
            Invites = invites,
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>()
        };
    }
}