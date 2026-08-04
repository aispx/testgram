using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

/// <summary>
/// Reorder <a href="https://corefork.telegram.org/api/stories#story-albums">story albums on a profile »</a>.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stories.reorderAlbums"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ReorderAlbumsHandler(
    IStoryAccessService storyAccessService,
    IStoryAlbumService storyAlbumService)
    : RpcResultObjectHandler<MyTelegram.Schema.Stories.RequestReorderAlbums, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Stories.RequestReorderAlbums obj)
    {
        var (ownerPeerId, ownerPeerType) =
            await storyAccessService.ResolveOwnedPeerAsync(obj.Peer, input.UserId, StoryRight.Edit);

        var order = obj.Order?.Distinct().ToList() ?? [];

        await storyAlbumService.ReorderAlbumsAsync(ownerPeerId, ownerPeerType, order);

        return new TBoolTrue();
    }
}
