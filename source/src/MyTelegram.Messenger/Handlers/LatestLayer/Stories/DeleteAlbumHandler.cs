using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

/// <summary>
/// Delete a <a href="https://corefork.telegram.org/api/stories#story-albums">story album</a>. The stories
/// themselves are kept — only the album and its membership references go away.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stories.deleteAlbum"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class DeleteAlbumHandler(
    IStoryAccessService storyAccessService,
    IStoryAlbumService storyAlbumService)
    : RpcResultObjectHandler<MyTelegram.Schema.Stories.RequestDeleteAlbum, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Stories.RequestDeleteAlbum obj)
    {
        var (ownerPeerId, ownerPeerType) =
            await storyAccessService.ResolveOwnedPeerAsync(obj.Peer, input.UserId, StoryRight.Edit);

        await storyAlbumService.DeleteAlbumAsync(ownerPeerId, ownerPeerType, obj.AlbumId);

        return new TBoolTrue();
    }
}
