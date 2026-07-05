namespace MyTelegram.Messenger.Handlers.LatestLayer.Contacts;
/// <summary>
/// Replace the contents of an entire <a href="https://corefork.telegram.org/api/block">blocklist, see here for more info »</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/contacts.setBlocked"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SetBlockedHandler(
    IPeerHelper peerHelper,
    IBlockCacheAppService blockCacheAppService,
    IObjectMessageSender objectMessageSender)
    : RpcResultObjectHandler<MyTelegram.Schema.Contacts.RequestSetBlocked, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Contacts.RequestSetBlocked obj)
    {
        var newPeers = obj.Id
            .Select(p => peerHelper.GetPeer(p, input.UserId))
            .Where(p => p is { PeerType: PeerType.User or PeerType.Chat or PeerType.Channel } && p.PeerId != input.UserId)
            .DistinctBy(p => (p.PeerType, p.PeerId))
            .ToList();

        var oldPage = await blockCacheAppService.GetBlockedAsync(input.UserId, 0, int.MaxValue, obj.MyStoriesFrom);
        var oldPeers = oldPage.Items.Select(p => new Peer(p.TargetPeerType, p.TargetPeerId)).ToHashSet();
        var newPeerSet = newPeers.ToHashSet();

        await blockCacheAppService.ReplaceBlockedAsync(input.UserId, newPeers, obj.MyStoriesFrom);

        var updates = new List<IUpdate>();
        updates.AddRange(newPeers
            .Where(p => !oldPeers.Contains(p))
            .Select(p => ToUpdate(p, true, obj.MyStoriesFrom)));
        updates.AddRange(oldPeers
            .Where(p => !newPeerSet.Contains(p))
            .Select(p => ToUpdate(p, false, obj.MyStoriesFrom)));

        if (updates.Count > 0)
        {
            await objectMessageSender.PushMessageToPeerAsync(
                new Peer(PeerType.User, input.UserId),
                new TUpdates
                {
                    Updates = [.. updates],
                    Users = [],
                    Chats = [],
                    Date = CurrentDate,
                },
                excludeAuthKeyId: input.AuthKeyId);
        }

        return new TBoolTrue();
    }

    private static TUpdatePeerBlocked ToUpdate(Peer peer, bool blocked, bool myStoriesFrom) =>
        new()
        {
            Blocked = blocked,
            BlockedMyStoriesFrom = myStoriesFrom,
            PeerId = peer.PeerType switch
            {
                PeerType.Channel => new TPeerChannel { ChannelId = peer.PeerId },
                PeerType.Chat => new TPeerChat { ChatId = peer.PeerId },
                _ => new TPeerUser { UserId = peer.PeerId },
            },
        };
}
