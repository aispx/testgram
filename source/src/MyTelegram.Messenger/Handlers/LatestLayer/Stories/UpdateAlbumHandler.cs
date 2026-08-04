using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

/// <summary>
/// Update a <a href="https://corefork.telegram.org/api/stories#story-albums">story album</a>.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 STORY_ID_INVALID The specified story ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stories.updateAlbum"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class UpdateAlbumHandler(
    IStoryAccessService storyAccessService,
    IStoryAlbumService storyAlbumService)
    : RpcResultObjectHandler<RequestUpdateAlbum, IStoryAlbum>
{
    protected override async Task<IStoryAlbum> HandleCoreAsync(IRequestInput input, RequestUpdateAlbum obj)
    {
        var (ownerPeerId, ownerPeerType) =
            await storyAccessService.ResolveOwnedPeerAsync(obj.Peer, input.UserId, StoryRight.Edit);

        var album = await storyAlbumService.GetAlbumAsync(ownerPeerId, ownerPeerType, obj.AlbumId);
        if (album == null)
        {
            RpcErrors.RpcErrors400.StoryIdInvalid.ThrowRpcError();
        }

        if (obj.DeleteStories is { Count: > 0 })
        {
            await storyAlbumService.RemoveStoriesAsync(
                ownerPeerId, ownerPeerType, obj.AlbumId, obj.DeleteStories.Distinct().ToList());
        }

        if (obj.AddStories is { Count: > 0 })
        {
            await storyAlbumService.AddStoriesAsync(
                ownerPeerId, ownerPeerType, obj.AlbumId, obj.AddStories.Distinct().ToList());
        }

        if (!string.IsNullOrEmpty(obj.Title))
        {
            await storyAlbumService.SetTitleAsync(ownerPeerId, ownerPeerType, obj.AlbumId, obj.Title);
        }

        if (obj.Order is { Count: > 0 })
        {
            await storyAlbumService.SetStoryOrderAsync(
                ownerPeerId, ownerPeerType, obj.AlbumId, obj.Order.ToList());
        }

        var updated = await storyAlbumService.GetAlbumAsync(ownerPeerId, ownerPeerType, obj.AlbumId);

        return await storyAlbumService.ToStoryAlbumAsync(updated ?? album!);
    }
}
