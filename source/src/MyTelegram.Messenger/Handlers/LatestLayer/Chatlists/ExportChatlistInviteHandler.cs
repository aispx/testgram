using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Chatlists;

/// <summary>
/// Export a <a href="https://corefork.telegram.org/api/folders">folder »</a>, creating a <a href="https://corefork.telegram.org/api/links#chat-folder-links">chat folder deep link »</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/chatlists.exportChatlistInvite"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ExportChatlistInviteHandler : RpcResultObjectHandler<MyTelegram.Schema.Chatlists.RequestExportChatlistInvite, MyTelegram.Schema.Chatlists.IExportedChatlistInvite>
{
    private readonly IMongoDatabase _database;
    private readonly IQueryProcessor _queryProcessor;
    private readonly IPeerHelper _peerHelper;
    private readonly ILayeredService<IDialogFilterConverter> _dialogFilterLayeredService;

    public ExportChatlistInviteHandler(
        IMongoDatabase database,
        IQueryProcessor queryProcessor,
        IPeerHelper peerHelper,
        ILayeredService<IDialogFilterConverter> dialogFilterLayeredService)
    {
        _database = database;
        _queryProcessor = queryProcessor;
        _peerHelper = peerHelper;
        _dialogFilterLayeredService = dialogFilterLayeredService;
    }

    protected override async Task<MyTelegram.Schema.Chatlists.IExportedChatlistInvite> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Chatlists.RequestExportChatlistInvite obj)
    {
        // 1. Validate chatlist
        if (obj.Chatlist is not TInputChatlistDialogFilter chatlistFilter)
        {
            RpcErrors.RpcErrors400.FilterIdInvalid.ThrowRpcError();
            return null!;
        }

        var filterId = chatlistFilter.FilterId;

        // 2. Check if filter exists and belongs to user
        var filterReadModels = await _queryProcessor.ProcessAsync(
            new GetDialogFiltersQuery(input.UserId),
            CancellationToken.None);

        var filter = filterReadModels.FirstOrDefault(f => f.Filter.Id == filterId);
        if (filter == null)
        {
            RpcErrors.RpcErrors400.FilterIdInvalid.ThrowRpcError();
            return null!;
        }

        // 3. Validate peers (must be channels/groups)
        if (obj.Peers.Count == 0)
        {
            RpcErrors.RpcErrors400.PeersListEmpty.ThrowRpcError();
        }

        var peerIds = new List<long>();
        var peerTypes = new List<string>();

        foreach (var inputPeer in obj.Peers)
        {
            var peer = _peerHelper.GetPeer(inputPeer, input.UserId);

            if (peer.PeerType == PeerType.User)
            {
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            }

            peerIds.Add(peer.PeerId);
            peerTypes.Add(peer.PeerType.ToString());
        }

        // 4. Generate unique slug
        var slug = GenerateSlug();

        // 5. Store invite in MongoDB
        var collection = _database.GetCollection<BsonDocument>("chatlist_invites");

        var inviteDoc = new BsonDocument
        {
            ["_id"] = $"chatlist-invite-{slug}",
            ["Slug"] = slug,
            ["CreatorUserId"] = input.UserId,
            ["FilterId"] = filterId,
            ["Title"] = obj.Title,
            ["PeerIds"] = new BsonArray(peerIds),
            ["PeerTypes"] = new BsonArray(peerTypes),
            ["CreatedDate"] = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["Revoked"] = false
        };

        await collection.InsertOneAsync(inviteDoc);

        // 6. Build response
        var url = $"https://t.me/addlist/{slug}";

        var peers = new TVector<IPeer>();
        for (int i = 0; i < peerIds.Count; i++)
        {
            var peerType = Enum.Parse<PeerType>(peerTypes[i]);
            peers.Add(new Peer(peerType, peerIds[i]).ToPeer());
        }

        var invite = new MyTelegram.Schema.TExportedChatlistInvite
        {
            Title = obj.Title,
            Url = url,
            Peers = peers
        };

        return new MyTelegram.Schema.Chatlists.TExportedChatlistInvite
        {
            Filter = _dialogFilterLayeredService.GetConverter(input.Layer).ToDialogFilter(filter.Filter),
            Invite = invite
        };
    }

    private static string GenerateSlug()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var random = new Random();
        return new string(Enumerable.Repeat(chars, 16)
            .Select(s => s[random.Next(s.Length)]).ToArray());
    }
}
