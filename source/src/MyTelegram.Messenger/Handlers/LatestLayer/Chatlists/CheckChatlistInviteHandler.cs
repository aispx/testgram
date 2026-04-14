using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Chatlists;
/// <summary>
/// Obtain information about a <a href="https://corefork.telegram.org/api/links#chat-folder-links">chat folder deep link »</a>.
/// Possible errors
/// Code Type Description
/// 400 INVITE_SLUG_EMPTY The specified invite slug is empty.
/// 400 INVITE_SLUG_EXPIRED The specified chat folder link has expired.
/// <para><c>See <a href="https://corefork.telegram.org/method/chatlists.checkChatlistInvite"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class CheckChatlistInviteHandler : RpcResultObjectHandler<MyTelegram.Schema.Chatlists.RequestCheckChatlistInvite, MyTelegram.Schema.Chatlists.IChatlistInvite>
{
    private readonly IMongoDatabase _database;
    private readonly IQueryProcessor _queryProcessor;

    public CheckChatlistInviteHandler(
        IMongoDatabase database,
        IQueryProcessor queryProcessor)
    {
        _database = database;
        _queryProcessor = queryProcessor;
    }

    protected override async Task<MyTelegram.Schema.Chatlists.IChatlistInvite> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Chatlists.RequestCheckChatlistInvite obj)
    {
        // 1. Validate slug
        if (string.IsNullOrWhiteSpace(obj.Slug))
        {
            RpcErrors.RpcErrors400.InviteSlugEmpty.ThrowRpcError();
        }

        // 2. Find invite in MongoDB
        var collection = _database.GetCollection<BsonDocument>("chatlist_invites");
        var filter = Builders<BsonDocument>.Filter.Eq("Slug", obj.Slug);
        var inviteDoc = await collection.Find(filter).FirstOrDefaultAsync();

        if (inviteDoc == null || inviteDoc["Revoked"].AsBoolean)
        {
            RpcErrors.RpcErrors400.InviteSlugExpired.ThrowRpcError();
        }

        var filterId = inviteDoc["FilterId"].AsInt32;
        var title = inviteDoc["Title"].AsString;
        var peerIds = inviteDoc["PeerIds"].AsBsonArray.Select(p => p.AsInt64).ToList();
        var peerTypes = inviteDoc["PeerTypes"].AsBsonArray.Select(p => p.AsString).ToList();

        // 3. Check if user already has this folder
        var userFilters = await _queryProcessor.ProcessAsync(
            new GetDialogFiltersQuery(input.UserId),
            CancellationToken.None);

        var existingFilter = userFilters.FirstOrDefault(f => f.FolderId == filterId);

        if (existingFilter != null)
        {
            // User already has this folder - return chatlistInviteAlready
            var alreadyPeers = new TVector<IPeer>();
            var missingPeers = new TVector<IPeer>();

            for (int i = 0; i < peerIds.Count; i++)
            {
                var peerType = Enum.Parse<PeerType>(peerTypes[i]);
                var peer = new Peer(peerType, peerIds[i]).ToPeer();
                alreadyPeers.Add(peer);
            }

            return new MyTelegram.Schema.Chatlists.TChatlistInviteAlready
            {
                FilterId = filterId,
                MissingPeers = missingPeers,
                AlreadyPeers = alreadyPeers,
                Chats = new TVector<IChat>(),
                Users = new TVector<IUser>()
            };
        }

        // 4. User doesn't have folder - return chatlistInvite
        var peers = new TVector<IPeer>();
        for (int i = 0; i < peerIds.Count; i++)
        {
            var peerType = Enum.Parse<PeerType>(peerTypes[i]);
            peers.Add(new Peer(peerType, peerIds[i]).ToPeer());
        }

        return new MyTelegram.Schema.Chatlists.TChatlistInvite
        {
            Title = new TTextWithEntities
            {
                Text = title,
                Entities = new TVector<IMessageEntity>()
            },
            Peers = peers,
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>()
        };
    }
}