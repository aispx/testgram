using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

/// <summary>
/// Create a <a href="https://corefork.telegram.org/api/stories#story-albums">story album</a>.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stories.createAlbum"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class CreateAlbumHandler(
    IStoryAccessService storyAccessService,
    IStoryAlbumService storyAlbumService)
    : RpcResultObjectHandler<RequestCreateAlbum, IStoryAlbum>
{
    protected override async Task<IStoryAlbum> HandleCoreAsync(IRequestInput input, RequestCreateAlbum obj)
    {
        var (ownerPeerId, ownerPeerType) =
            await storyAccessService.ResolveOwnedPeerAsync(obj.Peer, input.UserId, StoryRight.Edit);

        if (string.IsNullOrWhiteSpace(obj.Title))
        {
            RpcErrors.RpcErrors400.TitleInvalid.ThrowRpcError();
        }

        var storyIds = obj.Stories?.Distinct().ToList() ?? [];

        var album = await storyAlbumService.CreateAlbumAsync(ownerPeerId, ownerPeerType, obj.Title, storyIds);

        return await storyAlbumService.ToStoryAlbumAsync(album);
    }
}
