using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Chatlists;
/// <summary>
/// Import a <a href="https://corefork.telegram.org/api/links#chat-folder-links">chat folder deep link »</a>, joining some or all the chats in the folder.
/// Possible errors
/// Code Type Description
/// 400 CHANNELS_TOO_MUCH You have joined too many channels/supergroups.
/// 400 CHATLISTS_TOO_MUCH You have created too many folder links, hitting the <code>chatlist_invites_limit_default</code>/<code>chatlist_invites_limit_premium</code> <a href="https://corefork.telegram.org/api/config#chatlist-invites-limit-default">limits »</a>.
/// 400 FILTER_INCLUDE_EMPTY The include_peers vector of the filter is empty.
/// 400 INVITE_SLUG_EMPTY The specified invite slug is empty.
/// 400 INVITE_SLUG_EXPIRED The specified chat folder link has expired.
/// <para><c>See <a href="https://corefork.telegram.org/method/chatlists.joinChatlistInvite"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class JoinChatlistInviteHandler : RpcResultObjectHandler<MyTelegram.Schema.Chatlists.RequestJoinChatlistInvite, MyTelegram.Schema.IUpdates>
{
    private readonly IMongoDatabase _database;
    private readonly ICommandBus _commandBus;
    private readonly IPeerHelper _peerHelper;

    public JoinChatlistInviteHandler(
        IMongoDatabase database,
        ICommandBus commandBus,
        IPeerHelper peerHelper)
    {
        _database = database;
        _commandBus = commandBus;
        _peerHelper = peerHelper;
    }

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Chatlists.RequestJoinChatlistInvite obj)
    {
        // 1. Find invite
        var collection = _database.GetCollection<BsonDocument>("chatlist_invites");
        var filter = Builders<BsonDocument>.Filter.Eq("Slug", obj.Slug);
        var inviteDoc = await collection.Find(filter).FirstOrDefaultAsync();

        if (inviteDoc == null || inviteDoc["Revoked"].AsBoolean)
        {
            RpcErrors.RpcErrors400.InviteSlugExpired.ThrowRpcError();
        }

        var filterId = inviteDoc["FilterId"].AsInt32;
        var title = inviteDoc["Title"].AsString;

        // 2. Convert selected peers to InputPeer
        var includePeers = new List<InputPeer>();
        foreach (var inputPeer in obj.Peers)
        {
            var peer = _peerHelper.GetPeer(inputPeer, input.UserId);
            long accessHash = 0;

            switch (inputPeer)
            {
                case TInputPeerChannel inputPeerChannel:
                    accessHash = inputPeerChannel.AccessHash;
                    break;
                case TInputPeerUser inputPeerUser:
                    accessHash = inputPeerUser.AccessHash;
                    break;
            }

            includePeers.Add(new InputPeer(peer, accessHash));
        }

        // 3. Create dialogFilterChatlist
        var dialogFilter = new DialogFilter(
            filterId,
            false, // Contacts
            false, // NonContacts
            false, // Groups
            false, // Broadcasts
            false, // Bots
            false, // ExcludeMuted
            false, // ExcludeRead
            false, // ExcludeArchived
            false, // TitleNoAnimate
            new TTextWithEntities { Text = title, Entities = new TVector<IMessageEntity>() },
            null, // Emoticon
            null, // Color
            new List<InputPeer>(), // PinnedPeers
            includePeers,
            new List<InputPeer>(), // ExcludePeers
            true // IsChatlist
        );

        // 4. Create folder via command
        var command = new UpdateDialogFilterCommand(
            DialogFilterId.Create(input.UserId, filterId),
            input.ToRequestInfo(),
            input.UserId,
            dialogFilter
        );

        await _commandBus.PublishAsync(command, default);

        // 5. Return empty Updates (folder will sync via updateDialogFilter)
        return new TUpdates
        {
            Updates = new TVector<IUpdate>(),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Seq = 0
        };
    }
}