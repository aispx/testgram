namespace MyTelegram.Messenger.Services.Caching;

public interface IBlockCacheAppService
{
    Task BlockAsync(long userId,
        long targetPeerId,
        PeerType targetPeerType = PeerType.User,
        bool myStoriesFrom = false);

    Task<BlockedPeerCachePage> GetBlockedAsync(long userId,
        int offset,
        int limit,
        bool myStoriesFrom = false);

    //Task<bool> IsBlockedAsync(long userId,
    //    int targetPeerId);
    Task<bool> IsBlockedAsync(long userId,
        long targetPeerId);

    Task UnblockAsync(long userId,
        long targetPeerId,
        PeerType targetPeerType = PeerType.User,
        bool myStoriesFrom = false);

    Task ReplaceBlockedAsync(long userId,
        IReadOnlyCollection<Peer> peers,
        bool myStoriesFrom = false);

    //Task LoadAllBlockedAsync();
}

public sealed record BlockedPeerCacheItem(PeerType TargetPeerType, long TargetPeerId, int Date);

public sealed record BlockedPeerCachePage(int Count, IReadOnlyCollection<BlockedPeerCacheItem> Items);
