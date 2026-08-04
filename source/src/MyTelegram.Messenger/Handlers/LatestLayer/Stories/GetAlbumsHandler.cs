using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

/// <summary>
/// Fetch the <a href="https://corefork.telegram.org/api/stories#story-albums">story albums</a> of a profile.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stories.getAlbums"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetAlbumsHandler(
    IStoryAccessService storyAccessService,
    IStoryAlbumService storyAlbumService)
    : RpcResultObjectHandler<RequestGetAlbums, IAlbums>
{
    protected override async Task<IAlbums> HandleCoreAsync(IRequestInput input, RequestGetAlbums obj)
    {
        var (peerId, peerType) = await storyAccessService.ResolveReadablePeerAsync(obj.Peer, input.UserId);

        var albums = await storyAlbumService.GetAlbumsAsync(peerId, peerType);

        var hash = ComputeHash(albums);
        if (obj.Hash != 0 && obj.Hash == hash)
        {
            return new TAlbumsNotModified();
        }

        var converted = await storyAlbumService.ToStoryAlbumListAsync(albums);

        return new TAlbums
        {
            Albums = new TVector<IStoryAlbum>(converted),
            Hash = hash
        };
    }

    /// <summary>
    /// The <a href="https://corefork.telegram.org/api/offsets#hash-generation">standard hash</a> over the
    /// album ids and their covers, so that a re-ordered or re-covered album busts the client's cache.
    /// </summary>
    private static long ComputeHash(List<StoryAlbumDocument> albums)
    {
        var hash = 0L;

        foreach (var album in albums)
        {
            foreach (var value in new[] { (long)album.AlbumId, album.IconStoryId, album.Order })
            {
                hash ^= hash >> 21;
                hash ^= hash << 35;
                hash ^= hash >> 4;
                hash += value;
            }
        }

        return hash;
    }
}
